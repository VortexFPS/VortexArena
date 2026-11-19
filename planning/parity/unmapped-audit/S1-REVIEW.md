# S1 source review

Xonotic data pin `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`. The first pass selected 65 unresolved and 113 native-counterpart entries. This review retains those 178 paths in directory order as their [inventory dispositions](dispositions.json) change. The [authored notes](s1-review.txt) supply each disposition, comparison, source witness and open dimension.

**Outcomes** · 33 architecture-replaced, 127 counterpart, 13 counterpart-with-delta, 3 no-stock-caller-evidenced, 2 stock-disabled.

Counterpart identifies a native responsibility with some static evidence. Gap records an observed difference or a missing consumer after scoped search. Architecture decisions explain implementation boundaries. Behavioral equality requires paired runtime evidence for the relevant inputs. Include routes are possible stock build routes from the original ledger. The cited witness can be a caller, registration, implementation, or diagnostic anchor.

### 1. `client/shownames.qh`

**Disposition** · counterpart-with-delta · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Paired label occlusion, overlap and model-alpha trials](../shownames/README.md)

Paired trials exercised overlap fading, world occlusion and enemy fade-in. Native listen and remote clients hid tags behind a solid inline brush and restored them on removal. Both hid the Running Guns body and name. The stock ShowNames_Draw hook has no shipped subscriber.

**Open** · The native fixture ran at 20 Hz. Remote traces use mounted brushes at the last accepted pose. Under contention the listen trace can use static collision. The dark captures do not resolve the weapon silhouette.

**Sources** · [client/shownames.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/shownames.qh) · [client/view.qc:1860](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/view.qc#L1860) · [ShowNamesLayer.cs:287](../../../game/client/ShowNamesLayer.cs#L287)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L13))

### 2. `client/teamradar.qh`

**Disposition** · counterpart-with-delta · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Paired radar pulse, projection and click traces](../quickmenu-radar/TEAMRADAR.md)

Both games recorded 36 pings on one waypoint, overlapped three pulses, wrapped the 32 slots, matched custom map bounds and brightened grey over the HUD backdrop. Paired clicks landed within 0.04 units. Native rejected a delayed visible update after the reliable clear. The joined native client also cleared six Onslaught radar links after a dropped datagram and rejected a stale graph.

**Open** · Frame-by-frame zoom timing and a superseded pulse during packet loss remain unmeasured.

**Sources** · [client/teamradar.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/teamradar.qh) · [client/hud/panel/radar.qc:178](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/hud/panel/radar.qc#L178) · [ClientNet.cs:1087](../../../game/net/ClientNet.cs#L1087)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L11) → [client/teamradar.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/teamradar.qc#L1))

### 3. `client/hud/crosshair.qh`

**Disposition** · counterpart-with-delta · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Paired remote crosshair gates and chase captures](../crosshair/README.md)

Remote stock and native hid the reticle for the tested zero settings, view lock and held scores. At 800 × 600 the additive Vortex ring matched stock bounds at full and 62.5% charge. Native followed an accepted raw pose with interpolation disabled. Withholding both poses cleared the projection and restored body opacity. Both recovered when the raw pose returned.

**Open** · The ten-transition native replay used a stationary player, an instrumented pose outage and renderer state for opacity. Ordinary Forward+ geometry has visual evidence. Other weapon reticles and vehicles are separate.

**Sources** · [client/hud/crosshair.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/hud/crosshair.qh) · [client/view.qc:1039](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/view.qc#L1039) · [NetGame.cs:7137](../../../game/net/NetGame.cs#L7137)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L5) → [client/hud/_mod.qh:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/hud/_mod.qh#L2))

### 4. `client/hud/panel/scoreboard.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Paired tiebreaks, ignored chat and modal input](../scoreboard-blocks/README.md)

Fully tied players retained the pinned descending client-slot order across a native reconnect. Ignore and unignore changed chat delivery and the local icon. In paired games a living player dismissed the clickable radar with Tab. For a dead player, the assigned score key temporarily hid radar and released it on key-up.

**Open** · Team-score tiebreaks, spectator and downloader ordering, and permanent identity-based ignores remain open. The paired checks used local multiplayer.

**Sources** · [client/hud/panel/scoreboard.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/hud/panel/scoreboard.qh) · [client/view.qc:1870](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/view.qc#L1870) · [ScoreboardPanel.cs:290](../../../game/hud/ScoreboardPanel.cs#L290)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L2) → [client/announcer.qc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/announcer.qc#L5))

### 5. `common/_all.inc`

**Disposition** · architecture-replaced · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Release-client retries and connection deadlines](connection-timeout/README.md)

The QC header declares net_connecttimeout. The original engine applies it when opening a connection. The native ENet client uses it while waiting for handshake acceptance after loading, with a ten-second minimum. The legacy protocol path now samples its configured connect and message deadlines separately. Paired silent-client trials, successful signons and a server slot test exercise these boundaries.

**Open** · The release client sends twelve initial attempts and the newer upstream engine sends ten. The existing DP7 client follows the newer retry count. The silent-client trials measured outgoing attempts and the native failure callback. Stock menu failure presentation and loss during subsequent map loading remain unmeasured.

**Sources** · [common/_all.inc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc) · [common/_all.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L1) · [NetGame.cs:794](../../../game/net/NetGame.cs#L794)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15))
**Applicable decisions** · [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md) · [ADR-0018](../../decisions/ADR-0018-config-layer.md) · [ADR-0019](../../decisions/ADR-0019-legacy-compatibility-mode.md)

### 6. `common/constants.qh`

**Disposition** · counterpart-with-delta · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Corpse hit traces, species, ballots and door initialization](shared-constants/README.md)

The pinned Gak corpse shrank from max Z 45 to 5 at the end of the death tick. Native hit traces exercised the same standing and crouched rules. Native resolves species for players and cloned bodies, reserves one abstain slot in a 20-entry ballot and resolves door targets before moving and linking start-open doors.

**Open** · The original uses frag sentinels for participation and the native host has explicit player state. Remote species-specific gib playback, inherited render flags and the other map initialization phases remain open.

**Sources** · [common/constants.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/constants.qh) · [server/world.qc:2536](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/world.qc#L2536) · [PlayerBody.cs:14](../../../src/VortexArena.Common/Gameplay/Player/PlayerBody.cs#L14)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L9) → [client/shownames.qc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/shownames.qc#L6)) · MENUQC ([menu/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L5) → [menu/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/_mod.inc#L2) → [menu/draw.qc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/draw.qc#L3)) · SVQC ([server/progs.inc:22](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L22) → [lib/warpzone/server.qc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/warpzone/server.qc#L5))
**Applicable decisions** · [ADR-0007](../../decisions/ADR-0007-entity-model.md) · [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md)

### 7. `common/debug.qh`

**Disposition** · counterpart-with-delta · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Paired local entity and waypoint labels](../debug-labels/README.md)

The default-build entity and waypoint label producers have graphical listen-host counterparts. Stock and native captures show both label types and expiry. Additional native frames verify pause, quarter speed, default-off state and Waypoint tool priority. The original debugdraw/debugtrace compile switches remain off.

**Open** · Stock broadcasts debug events to connected peers. Native labels currently use local host and editor state. Native buffer queries cannot inspect handles owned by the legacy VM.

**Sources** · [common/debug.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/debug.qh) · [common/debug.qh:246](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/debug.qh#L246) · [NetGame.DebugLabels.cs:28](../../../game/net/NetGame.DebugLabels.cs#L28)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:35](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L35)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:35](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L35)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:35](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L35))
**Applicable decisions** · [ADR-0019](../../decisions/ADR-0019-legacy-compatibility-mode.md)

### 8. `common/net_notice.qc`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Receipt, delayed presentation and expiry](client-state-notices/README.md#notice-delivery-and-expiry)

Both servers schedule per-recipient notices after one second. In both clients a three-second notice appeared on release from a 1.5-second hold with two seconds on its countdown and expired unseen during a four-second hold.

**Open** · Native used real ENet loading. Stock held the draw call after CSQC receipt. The first possible receipt during stock signon and long pre-snapshot gaps remain unmeasured.

**Sources** · [common/net_notice.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/net_notice.qc) · [server/client.qc:1228](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/client.qc#L1228) · [ServerNet.cs:2467](../../../game/net/ServerNet.cs#L2467)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L9)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L9))
**Applicable decisions** · [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md)

### 9. `common/net_notice.qh`

**Disposition** · counterpart · inventory `declarations`.
**Evidence quality** · Static source comparison. [Receipt, delayed presentation and expiry](client-state-notices/README.md#notice-delivery-and-expiry)

The sole pinned stock caller sends the joining player configured pipe-separated messages. Two native clients displayed their own messages, and the countdown stopped with the server clock. The stock broadcast helper has no caller outside its declaration and definition.

**Open** · Native sends one reliable, grouped notice. Custom modal producers have no stock runtime comparison.

**Sources** · [common/net_notice.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/net_notice.qh) · [common/net_notice.qc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/net_notice.qc#L8) · [JoinNotice.cs:6](../../../src/VortexArena.Net/JoinNotice.cs#L6)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:30](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L30)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L7) → [server/client.qc:30](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/client.qc#L30))
**Applicable decisions** · [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md) · [ADR-0019](../../decisions/ADR-0019-legacy-compatibility-mode.md)

### 10. `common/state.qh`

**Disposition** · architecture-replaced · inventory `architecture-replaced`.
**Evidence quality** · Static source comparison. [Native connection and player-state lifetimes](client-state-notices/README.md)

Native keeps client preferences through respawn and observation and clears them at disconnect. The verified identity reaches the connect hooks for mute and vote privileges. A fresh client receives the pinned idle grace. Existing replacement-connection and hitplot checks cover preference isolation and writer teardown.

**Open** · Native map rehosting creates a new connection. Same-channel level parameter transfer and external player statistics remain separate from the local lifecycle checks.

**Sources** · [common/state.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/state.qh) · [common/state.qc:46](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/state.qc#L46) · [GameWorld.cs:2051](../../../src/VortexArena.Server/GameWorld.cs#L2051)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L2) → [server/anticheat.qc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/anticheat.qc#L4))
**Applicable decisions** · [ADR-0007](../../decisions/ADR-0007-entity-model.md)

### 11. `common/viewloc.qc`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Paired remote cursor, brush and combined camera-snap trials](../view-location/FREEAIM.md)

Both remote games placed the owner's cursor at (250,200) and (550,320) in an 800×600 view. Native aim calculations matched the sampled stock world hits. The follower saw the aimed avatar without a cursor.

**Open** · The stock crossing included a raw 69.7-degree frame before snapping to 90. Cursor feedback, fades and snapshot latency remain open.

**Sources** · [common/viewloc.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/viewloc.qc) · [ecs/systems/physics.qc:45](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/ecs/systems/physics.qc#L45) · [ViewLocationState.cs:35](../../../src/VortexArena.Common/Gameplay/MapObjects/ViewLocationState.cs#L35)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:28](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L28)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:28](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L28))

### 12. `common/viewloc.qh`

**Disposition** · counterpart · inventory `declarations`.
**Evidence quality** · Static source comparison. [Paired remote cursor, brush and combined camera-snap trials](../view-location/FREEAIM.md)

The server clips the player hull against the authored brush. Two-client trials on a server-only replacement verified exit in the empty bounding-box corner, reentry, tracking and combined hard and close snap. Both native cameras held 90 degrees on return and reentry.

**Open** · The measured path used a stationary player at scripted positions. Timing between authoritative snapshots remains open.

**Sources** · [common/viewloc.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/viewloc.qh) · [lib/csqcmodel/cl_model.qc:283](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/csqcmodel/cl_model.qc#L283) · [ViewLocation.cs:171](../../../src/VortexArena.Common/Gameplay/MapObjects/ViewLocation.cs#L171)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L4) → [client/csqcmodel_hooks.qc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/csqcmodel_hooks.qc#L14)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L7) → [server/client.qc:39](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/client.qc#L39))
**Applicable decisions** · [ADR-0007](../../decisions/ADR-0007-entity-model.md) · [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md)

### 13. `common/effects/qc/casings.qh`

**Disposition** · counterpart-with-delta · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Shell and brass impact audio and final-second fade](../rubble-limits/casing-impact/README.md)

Paired trials on Stormkeep verified the stock server modes, shooter and follower preferences, and in-view versus out-of-view delivery to an independent player. A native follow release between firing and sending preserved the first-person flag. The client applied the configured gun offset only outside chase view. The native pool trims on spawn and caps allocation at 1,024. Separate fixed-step tests cover ticrate and residual motion. Stock and native impact recordings resolved the shell and brass sample families. Native reads the lifetime at spawn, fades in place through the final second and gates catch-up sounds by draw time.

**Open** · Exact shell-tag placement, moving-brush contacts and spatial gain remain open. Native translucent batches use Godot sorting.

**Sources** · [common/effects/qc/casings.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/casings.qh) · [common/effects/qc/casings.qc:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/casings.qc#L17) · [ServerNet.cs:4418](../../../game/net/ServerNet.cs#L4418)
**Include routes** · CSQC ([client/progs.inc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L8) → [common/effects/qc/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/_mod.inc#L2) → [common/effects/qc/casings.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/casings.qc#L1)) · SVQC ([server/progs.inc:16](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L16) → [common/effects/qc/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/_mod.inc#L2) → [common/effects/qc/casings.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/casings.qc#L1))

### 14. `common/effects/qc/gibs.qh`

**Disposition** · counterpart-with-delta · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Paired lethal hits under seven client settings](../rubble-limits/gib-options-live/README.md)

In seven paired lethal-hit cases, the ordinary corpse splash produced 12 models by default and 52 with EKG. Both games also tossed three separate impact chunks in those two cases. Native recorded two hit-particle requests of 200 by default and 1,000 with EKG, consistent with the executed stock branch. Both engines exercised the three gentle modes, particle-only replacement and suppression.

**Open** · The paired shots used human players. With normal client defaults, aligned native Original views showed Normal, gentle mode 2 and particle-only effects in both camera projections. Hiding the batch changed zero pixels under no-gibs. The other gentle modes have routing evidence; cross-engine pixel equivalence is unmeasured. Model trails, explosion push, gib audio, random sequences and end-of-life presentation retain differences or lack verification.

**Sources** · [common/effects/qc/gibs.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/gibs.qh) · [common/effects/qc/gibs.qc:207](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/gibs.qc#L207) · [EffectSystem.cs:648](../../../game/client/EffectSystem.cs#L648)
**Include routes** · CSQC ([client/progs.inc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L8) → [common/effects/qc/_mod.inc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/_mod.inc#L4) → [common/effects/qc/gibs.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/gibs.qc#L1)) · SVQC ([server/progs.inc:16](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L16) → [common/effects/qc/_mod.inc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/_mod.inc#L4) → [common/effects/qc/gibs.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/gibs.qc#L1))

### 15. `common/effects/qc/modeleffects.qc`

**Disposition** · counterpart-with-delta · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Paired Fireball blast, timeout and self-damage trials](../fireball-sphere/README.md)

The primary Fireball is the only live stock caller. The CTF shockwave is compiled out. On identical Stormkeep geometry, an eligible impact gave 28 stock observer draws and one native receipt per client. Timeout and owner self-damage produced no observed sphere. Native starts at age zero on receipt and fades the grid-lit model below 0.003 alpha.

**Open** · The cameras used quarter-speed game time with the native client scale set explicitly. Scene lighting and frame sampling differ. Stock linked-entity eligibility was checked in QC and the release binary. Native loopback delivered once to a peer accepted at 98 ms, and zero times at 100 or 125 ms. Native sends reliably; stock delivery depends on a gameplay datagram reaching a begun client during the window.

**Sources** · [common/effects/qc/modeleffects.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/modeleffects.qc) · [common/weapons/weapon/fireball.qc:31](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/weapon/fireball.qc#L31) · [EffectSystem.cs:282](../../../game/client/EffectSystem.cs#L282)
**Include routes** · CSQC ([client/progs.inc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L8) → [common/effects/qc/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/_mod.inc#L7)) · SVQC ([server/progs.inc:16](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L16) → [common/effects/qc/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/_mod.inc#L7))

### 16. `common/effects/qc/modeleffects.qh`

**Disposition** · counterpart-with-delta · inventory `declarations`.
**Evidence quality** · Static source comparison. [Paired Fireball blast, timeout and self-damage trials](../fireball-sphere/README.md)

The primary Fireball is the only live stock caller. The CTF shockwave is compiled out. On identical Stormkeep geometry, an eligible impact gave 28 stock observer draws and one native receipt per client. Timeout and owner self-damage produced no observed sphere. Native starts at age zero on receipt and fades the grid-lit model below 0.003 alpha.

**Open** · The cameras used quarter-speed game time with the native client scale set explicitly. Scene lighting and frame sampling differ. Stock linked-entity eligibility was checked in QC and the release binary. Native loopback delivered once to a peer accepted at 98 ms, and zero times at 100 or 125 ms. Native sends reliably; stock delivery depends on a gameplay datagram reaching a begun client during the window.

**Sources** · [common/effects/qc/modeleffects.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/modeleffects.qh) · [common/weapons/weapon/fireball.qc:31](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/weapon/fireball.qc#L31) · [EffectSystem.cs:282](../../../game/client/EffectSystem.cs#L282)
**Include routes** · CSQC ([client/progs.inc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L8) → [common/effects/qc/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/_mod.inc#L7) → [common/effects/qc/modeleffects.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/modeleffects.qc#L1)) · SVQC ([server/progs.inc:16](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L16) → [common/effects/qc/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/_mod.inc#L7) → [common/effects/qc/modeleffects.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/modeleffects.qc#L1))

### 17. `common/effects/qc/rubble.qc`

**Disposition** · counterpart-with-delta · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Electro cap, moving carrier, death and disconnect trials](../rubble-limits/electro-limit/README.md)

The oldest listed stuck Electro orb detonated despite its later fuse. An unlisted orb and the other owner's orb survived. Each client received one impact in damage mode 0 and three after the cap was lowered in mode 2. In a second trial an orb rode a player, fell on carrier death and was evicted by the next launch.

**Open** · With the same muzzle offset, carrier yaw and tick rate, both orbs moved to Y 460 and their heights retained the 1/32-unit difference measured at launch. Both engines preserved the detonation deadline on carrier death. Stock showed two disconnect outcomes. Native consistently removes a freed attachment. Rotating brushes and blast presentation were outside this trial.

**Sources** · [common/effects/qc/rubble.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/rubble.qc) · [common/weapons/weapon/electro.qc:439](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/weapon/electro.qc#L439) · [Electro.cs:646](../../../src/VortexArena.Common/Gameplay/Weapons/Electro.cs#L646)
**Include routes** · CSQC ([client/progs.inc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L8) → [common/effects/qc/_mod.inc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/_mod.inc#L8)) · SVQC ([server/progs.inc:16](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L16) → [common/effects/qc/_mod.inc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/_mod.inc#L8))

### 18. `common/effects/qc/rubble.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Paired lethal shotgun and native same-frame trials](../rubble-limits/network-order.md#row-18)

On a lethal shotgun hit, the stock observer's rubble list held same-timestamp gibs followed by the shell. Both native clients received the splash before the shell. A separate native same-frame shell, splash and bullet trial verified receive order and independent cap-one eviction.

**Open** · Native caps each ordinary pool at 1,024. The same-frame trial covered ordinary model gibs. Row 14 separately covers replacement particles.

**Sources** · [common/effects/qc/rubble.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/rubble.qh) · [common/effects/qc/rubble.qc:20](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/rubble.qc#L20) · [RubblePool.cs:39](../../../game/client/RubblePool.cs#L39)
**Include routes** · CSQC ([client/progs.inc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L8) → [common/effects/qc/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/_mod.inc#L2) → [common/effects/qc/casings.qc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/casings.qc#L8)) · SVQC ([server/progs.inc:16](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L16) → [common/effects/qc/_mod.inc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/_mod.inc#L8) → [common/effects/qc/rubble.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/effects/qc/rubble.qc#L1))

### 19. `common/gametypes/gametype/assault/assault.qc`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Paired Simplified Chinese Guide selections](../guide-gametypes/README.md)

Both Chinese Guide windows showed the role swap, first attackers’ time limit and objective-health waypoints with matching paragraph breaks.

**Open** · The paired check used Simplified Chinese in a framed 1100 × 700 Guide. Other locales and viewport sizes were outside this comparison.

**Sources** · [common/gametypes/gametype/assault/assault.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/assault/assault.qc) · [menu/xonotic/dialog_media_guide.qc:77](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/dialog_media_guide.qc#L77) · [GuideCatalogText.cs:14](../../../game/menu/dialogs/GuideCatalogText.cs#L14)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L3) → [common/gametypes/gametype/assault/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/assault/_mod.inc#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L3) → [common/gametypes/gametype/assault/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/assault/_mod.inc#L2)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L3) → [common/gametypes/gametype/assault/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/assault/_mod.inc#L2))

### 20. `common/gametypes/gametype/ctf/ctf.qc`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Paired Simplified Chinese Guide selections](../guide-gametypes/README.md)

Both Chinese Guide windows showed the home-flag condition, dropped-flag return and stalemate reveal, with the selected mode name in the first and fourth paragraphs.

**Open** · The paired check used Simplified Chinese in a framed 1100 × 700 Guide. Other locales and viewport sizes were outside this comparison.

**Sources** · [common/gametypes/gametype/ctf/ctf.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/ctf/ctf.qc) · [menu/xonotic/dialog_media_guide.qc:77](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/dialog_media_guide.qc#L77) · [GuideCatalogText.cs:29](../../../game/menu/dialogs/GuideCatalogText.cs#L29)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L5) → [common/gametypes/gametype/ctf/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/ctf/_mod.inc#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L5) → [common/gametypes/gametype/ctf/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/ctf/_mod.inc#L2)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L5) → [common/gametypes/gametype/ctf/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/ctf/_mod.inc#L2))

### 21. `common/gametypes/gametype/cts/cts.qc`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Paired Simplified Chinese Guide selections](../guide-gametypes/README.md)

Both Chinese Guide windows displayed the CTS and Race names in the same comparison. The native line wrapper now keeps the CTS prefix with its Chinese name at the observed width.

**Open** · The paired check used Simplified Chinese in a framed 1100 × 700 Guide. Other locales and viewport sizes were outside this comparison.

**Sources** · [common/gametypes/gametype/cts/cts.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/cts/cts.qc) · [menu/xonotic/dialog_media_guide.qc:77](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/dialog_media_guide.qc#L77) · [GuideCatalogText.cs:40](../../../game/menu/dialogs/GuideCatalogText.cs#L40)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L6) → [common/gametypes/gametype/cts/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/cts/_mod.inc#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L6) → [common/gametypes/gametype/cts/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/cts/_mod.inc#L2)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L6) → [common/gametypes/gametype/cts/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/cts/_mod.inc#L2))

### 22. `common/gametypes/gametype/domination/domination.qc`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Paired Simplified Chinese Guide selections](../guide-gametypes/README.md)

Both Chinese Guide windows described neutral points, touch capture and periodic scoring alongside frags, with matching line breaks.

**Open** · The paired check used Simplified Chinese in a framed 1100 × 700 Guide. Other locales and viewport sizes were outside this comparison.

**Sources** · [common/gametypes/gametype/domination/domination.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/domination/domination.qc) · [menu/xonotic/dialog_media_guide.qc:77](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/dialog_media_guide.qc#L77) · [GuideCatalogText.cs:55](../../../game/menu/dialogs/GuideCatalogText.cs#L55)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L8) → [common/gametypes/gametype/domination/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/domination/_mod.inc#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L8) → [common/gametypes/gametype/domination/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/domination/_mod.inc#L2)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L8) → [common/gametypes/gametype/domination/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/domination/_mod.inc#L2))

### 23. `common/gametypes/gametype/duel/duel.qc`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Paired Simplified Chinese Guide selections](../guide-gametypes/README.md)

Both Chinese Guide windows rendered the repeated Duel name, Deathmatch argument and translated XPM expansion. Mega Health was red, Mega Armor green and the following text white.

**Open** · The paired check used Simplified Chinese in a framed 1100 × 700 Guide. Other locales and viewport sizes were outside this comparison.

**Sources** · [common/gametypes/gametype/duel/duel.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/duel/duel.qc) · [menu/xonotic/dialog_media_guide.qc:77](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/dialog_media_guide.qc#L77) · [GuideCatalogText.cs:62](../../../game/menu/dialogs/GuideCatalogText.cs#L62)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L9) → [common/gametypes/gametype/duel/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/duel/_mod.inc#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L9) → [common/gametypes/gametype/duel/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/duel/_mod.inc#L2)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L9) → [common/gametypes/gametype/duel/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/duel/_mod.inc#L2))

### 24. `common/gametypes/gametype/duel/sv_duel.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Paired Duel powerup switches and alias-only map load](../duel-powerups/README.md)

The Duel map-spawn hook reads the resolved powerup definition. Both dedicated hosts suppressed all eight authored powerups by default, restored them at 1 and suppressed them after returning to 0. A fourth load resolved the two aliases to Shield and Speed. Health remained available throughout.

**Open** · The paired run used ordinary Duel on a Stormkeep entity overlay with global and individual powerups enabled. Extra Life has separate InstaGib rules.

**Sources** · [common/gametypes/gametype/duel/sv_duel.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/duel/sv_duel.qh) · [common/gametypes/gametype/duel/sv_duel.qc:20](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/duel/sv_duel.qc#L20) · [Duel.cs:121](../../../src/VortexArena.Common/Gameplay/GameTypes/Duel.cs#L121)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L5) → [server/chat.qc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/chat.qc#L3) → [common/gametypes/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.qh#L7) → [common/gametypes/gametype/_mod.qh:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.qh#L9) → [common/gametypes/gametype/duel/_mod.qh:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/duel/_mod.qh#L4))
**Applicable decisions** · [ADR-0003](../../decisions/ADR-0003-source-generators.md)

### 25. `common/gametypes/gametype/freezetag/freezetag.qc`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Paired Simplified Chinese Guide selections](../guide-gametypes/README.md)

Four Chinese paragraphs in both Guides covered round elimination, thawing, the grace period and team communication.

**Open** · The paired check used Simplified Chinese in a framed 1100 × 700 Guide. Other locales and viewport sizes were outside this comparison.

**Sources** · [common/gametypes/gametype/freezetag/freezetag.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/freezetag/freezetag.qc) · [menu/xonotic/dialog_media_guide.qc:77](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/dialog_media_guide.qc#L77) · [GuideCatalogText.cs:72](../../../game/menu/dialogs/GuideCatalogText.cs#L72)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L10) → [common/gametypes/gametype/freezetag/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/freezetag/_mod.inc#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L10) → [common/gametypes/gametype/freezetag/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/freezetag/_mod.inc#L2)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L10) → [common/gametypes/gametype/freezetag/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/freezetag/_mod.inc#L2))

### 26. `common/gametypes/gametype/invasion/invasion.qc`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Paired Simplified Chinese Guide selections](../guide-gametypes/README.md)

Both Guides showed the two Chinese paragraphs on monster kills, suppressed player damage and the round end conditions.

**Open** · The paired check used Simplified Chinese in a framed 1100 × 700 Guide. Other locales and viewport sizes were outside this comparison.

**Sources** · [common/gametypes/gametype/invasion/invasion.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/invasion/invasion.qc) · [menu/xonotic/dialog_media_guide.qc:77](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/dialog_media_guide.qc#L77) · [GuideCatalogText.cs:81](../../../game/menu/dialogs/GuideCatalogText.cs#L81)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L11) → [common/gametypes/gametype/invasion/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/invasion/_mod.inc#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L11) → [common/gametypes/gametype/invasion/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/invasion/_mod.inc#L2)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L11) → [common/gametypes/gametype/invasion/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/invasion/_mod.inc#L2))

### 27. `common/gametypes/gametype/keepaway/keepaway.qc`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Paired Simplified Chinese Guide selections](../guide-gametypes/README.md)

Both Guides repeated the localized mode name in the first and last paragraphs. The four paragraphs covered scoring, drops and teleportation of the unattended ball.

**Open** · The paired check used Simplified Chinese in a framed 1100 × 700 Guide. Other locales and viewport sizes were outside this comparison.

**Sources** · [common/gametypes/gametype/keepaway/keepaway.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/keepaway/keepaway.qc) · [menu/xonotic/dialog_media_guide.qc:77](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/dialog_media_guide.qc#L77) · [GuideCatalogText.cs:87](../../../game/menu/dialogs/GuideCatalogText.cs#L87)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L12) → [common/gametypes/gametype/keepaway/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/keepaway/_mod.inc#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L12) → [common/gametypes/gametype/keepaway/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/keepaway/_mod.inc#L2)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L12) → [common/gametypes/gametype/keepaway/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/keepaway/_mod.inc#L2))

### 28. `common/gametypes/gametype/keyhunt/keyhunt.qc`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Paired Simplified Chinese Guide selections](../guide-gametypes/README.md)

Both Guides showed the localized KeyHunt name twice and substituted the CTF name in the third paragraph about passing keys.

**Open** · The paired check used Simplified Chinese in a framed 1100 × 700 Guide. Other locales and viewport sizes were outside this comparison.

**Sources** · [common/gametypes/gametype/keyhunt/keyhunt.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/keyhunt/keyhunt.qc) · [menu/xonotic/dialog_media_guide.qc:77](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/dialog_media_guide.qc#L77) · [GuideCatalogText.cs:96](../../../game/menu/dialogs/GuideCatalogText.cs#L96)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L13) → [common/gametypes/gametype/keyhunt/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/keyhunt/_mod.inc#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L13) → [common/gametypes/gametype/keyhunt/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/keyhunt/_mod.inc#L2)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L13) → [common/gametypes/gametype/keyhunt/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/keyhunt/_mod.inc#L2))

### 29. `common/gametypes/gametype/keyhunt/keyhunt.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [KeyHunt catalog boundaries and authored match settings](../keyhunt-map/README.md)

Both dedicated catalogs admitted 12 qualifying starts at an entity-origin diameter of 5121. They excluded 11 starts at 5121 and 12 at 5120. Both hosts applied modern and positional team and match limits and honored the mode-specific point override, including zero.

**Open** · Native game-world tests exercised score and active team count. The native Create tab retained 600 points and three teams for Key Hunt while Deathmatch kept its frag limit of five. A separate-host test verified transfer to a fresh server store. The paired servers were driven through their operator consoles.

**Sources** · [common/gametypes/gametype/keyhunt/keyhunt.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/keyhunt/keyhunt.qh) · [common/gametypes/gametype/keyhunt/keyhunt.qh:30](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/keyhunt/keyhunt.qh#L30) · [MapInfoCatalog.cs:170](../../../src/VortexArena.Formats/Bsp/MapInfoCatalog.cs#L170)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L4) → [client/csqcmodel_hooks.qc:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/csqcmodel_hooks.qc#L10) → [common/gametypes/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.qh#L7) → [common/gametypes/gametype/_mod.qh:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.qh#L13) → [common/gametypes/gametype/keyhunt/_mod.qh:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/keyhunt/_mod.qh#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L13) → [common/gametypes/gametype/keyhunt/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/keyhunt/_mod.inc#L2) → [common/gametypes/gametype/keyhunt/keyhunt.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/keyhunt/keyhunt.qc#L1)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L5) → [server/chat.qc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/chat.qc#L3) → [common/gametypes/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.qh#L7) → [common/gametypes/gametype/_mod.qh:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.qh#L13) → [common/gametypes/gametype/keyhunt/_mod.qh:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/keyhunt/_mod.qh#L2))
**Applicable decisions** · [ADR-0003](../../decisions/ADR-0003-source-generators.md)

### 30. `common/gametypes/gametype/keyhunt/sv_keyhunt.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Round controller and bot target order](../keyhunt-controller/README.md)

Both dedicated servers spawned keys and enabled tracking three simulation seconds apart with each delay set to 1.25 seconds. The round also ran during indefinite warmup. Stock prepended the second key and entered the carried-key bot visibility helper before reveal. Both native bots selected opposing carriers before reveal.

**Open** · The paired fixture used two bots. Human readiness, reset boundaries, list ties and disabled tracking were checked in native tests. Full stock routes and matching absolute map clocks remain unverified.

**Sources** · [common/gametypes/gametype/keyhunt/sv_keyhunt.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/keyhunt/sv_keyhunt.qh) · [common/gametypes/gametype/keyhunt/sv_keyhunt.qh:37](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/keyhunt/sv_keyhunt.qh#L37) · [KeyHunt.cs:573](../../../src/VortexArena.Common/Gameplay/GameTypes/KeyHunt.cs#L573)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L5) → [server/chat.qc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/chat.qc#L3) → [common/gametypes/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.qh#L7) → [common/gametypes/gametype/_mod.qh:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.qh#L13) → [common/gametypes/gametype/keyhunt/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/keyhunt/_mod.qh#L7))
**Applicable decisions** · [ADR-0003](../../decisions/ADR-0003-source-generators.md)

### 31. `common/gametypes/gametype/lms/lms.qc`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC topics include the following. Finite lives with full loadout and no pickups, leading-player visibility and leader-only lifesteal. The native Guide now supplies the entry with the translated paragraph keys from the pinned callback.

**Open** · Check layout, translated placeholders and color runs in a non-English locale.

**Sources** · [common/gametypes/gametype/lms/lms.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/lms/lms.qc) · [menu/xonotic/dialog_media_guide.qc:77](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/dialog_media_guide.qc#L77) · [GuideCatalogText.cs:104](../../../game/menu/dialogs/GuideCatalogText.cs#L104)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L14) → [common/gametypes/gametype/lms/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/lms/_mod.inc#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L14) → [common/gametypes/gametype/lms/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/lms/_mod.inc#L2)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L14) → [common/gametypes/gametype/lms/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/lms/_mod.inc#L2))

### 32. `common/gametypes/gametype/mayhem/sv_mayhem.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

The mode-add callback initializes Mayhem. Both its frag logic and TMayhem call the shared scoring routine; native Mayhem resolves shared damage/kill weights by cvar prefix.

**Open** · Check first-score initialization, rounding, suppression and cross-mode resets.

**Sources** · [common/gametypes/gametype/mayhem/sv_mayhem.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/mayhem/sv_mayhem.qh) · [common/gametypes/gametype/mayhem/sv_mayhem.qc:346](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/mayhem/sv_mayhem.qc#L346) · [Mayhem.cs:353](../../../src/VortexArena.Common/Gameplay/GameTypes/Mayhem.cs#L353)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L5) → [server/chat.qc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/chat.qc#L3) → [common/gametypes/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.qh#L7) → [common/gametypes/gametype/_mod.qh:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.qh#L15) → [common/gametypes/gametype/mayhem/_mod.qh:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/mayhem/_mod.qh#L4))
**Applicable decisions** · [ADR-0003](../../decisions/ADR-0003-source-generators.md)

### 33. `common/gametypes/gametype/nexball/nexball.qc`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC topics include the following. Soccer touch versus basketball pass/intercept weapon and the possibility of own goals. The native Guide now supplies the entry with the translated paragraph keys from the pinned callback.

**Open** · Check layout, translated placeholders and color runs in a non-English locale.

**Sources** · [common/gametypes/gametype/nexball/nexball.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/nexball/nexball.qc) · [menu/xonotic/dialog_media_guide.qc:77](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/dialog_media_guide.qc#L77) · [GuideCatalogText.cs:124](../../../game/menu/dialogs/GuideCatalogText.cs#L124)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:16](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L16) → [common/gametypes/gametype/nexball/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/nexball/_mod.inc#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:16](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L16) → [common/gametypes/gametype/nexball/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/nexball/_mod.inc#L2)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:16](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L16) → [common/gametypes/gametype/nexball/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/nexball/_mod.inc#L2))

### 34. `common/gametypes/gametype/onslaught/cl_controlpoint.qh`

**Disposition** · architecture-replaced · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC receiver uses a distinct CP icon box and status/setup bits 4/8. Native manages a separate icon entity and sends normal entity state, so QC bit meanings and bounds must be compared at the rendered icon and hitbox consumers.

**Open** · Verify proxy size, build progress and health/color for a late join.

**Sources** · [common/gametypes/gametype/onslaught/cl_controlpoint.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/cl_controlpoint.qh) · [common/gametypes/gametype/onslaught/cl_controlpoint.qc:112](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/cl_controlpoint.qc#L112) · [OnslaughtControlPoint.cs:533](../../../src/VortexArena.Common/Gameplay/GameTypes/OnslaughtControlPoint.cs#L533)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L4) → [client/csqcmodel_hooks.qc:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/csqcmodel_hooks.qc#L10) → [common/gametypes/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.qh#L7) → [common/gametypes/gametype/_mod.qh:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.qh#L17) → [common/gametypes/gametype/onslaught/_mod.qh:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/_mod.qh#L4))
**Applicable decisions** · [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md)

### 35. `common/gametypes/gametype/onslaught/cl_generator.qh`

**Disposition** · architecture-replaced · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC receiver bounds and the native spawned generator collision limits share the -52/-52/-14 to 52/52/75 values. Native regular entity state replaces the QC status/setup bit pair.

**Open** · Compare CSQC visual selection, tint and interpolation to native snapshots.

**Sources** · [common/gametypes/gametype/onslaught/cl_generator.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/cl_generator.qh) · [common/gametypes/gametype/onslaught/cl_generator.qc:144](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/cl_generator.qc#L144) · [OnslaughtControlPoint.cs:182](../../../src/VortexArena.Common/Gameplay/GameTypes/OnslaughtControlPoint.cs#L182)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L4) → [client/csqcmodel_hooks.qc:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/csqcmodel_hooks.qc#L10) → [common/gametypes/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.qh#L7) → [common/gametypes/gametype/_mod.qh:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.qh#L17) → [common/gametypes/gametype/onslaught/_mod.qh:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/_mod.qh#L11))
**Applicable decisions** · [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md)

### 36. `common/gametypes/gametype/onslaught/onslaught.qh`

**Disposition** · architecture-replaced · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC advertises map support from a generator classname, default pointlimit 1/time 20 and two linked schemas. Native class activates the mode and normal snapshots carry model state with a separate radar-link stream.

**Open** · Verify menu detection and legacy two-number mapinfo parsing independently of graph simulation.

**Sources** · [common/gametypes/gametype/onslaught/onslaught.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/onslaught.qh) · [common/gametypes/gametype/onslaught/onslaught.qh:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/onslaught.qh#L12) · [Onslaught.cs:142](../../../src/VortexArena.Common/Gameplay/GameTypes/Onslaught.cs#L142)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L4) → [client/csqcmodel_hooks.qc:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/csqcmodel_hooks.qc#L10) → [common/gametypes/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.qh#L7) → [common/gametypes/gametype/_mod.qh:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.qh#L17) → [common/gametypes/gametype/onslaught/_mod.qh:16](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/_mod.qh#L16)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:47](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L47) → [common/weapons/_all.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/_all.inc#L2) → [common/weapons/all.qc:20](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.qc#L20) → [common/weapons/weapon/_mod.inc:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/weapon/_mod.inc#L12) → [common/weapons/weapon/minelayer.qc:546](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/weapon/minelayer.qc#L546)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L5) → [server/chat.qc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/chat.qc#L3) → [common/gametypes/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.qh#L7) → [common/gametypes/gametype/_mod.qh:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.qh#L17) → [common/gametypes/gametype/onslaught/_mod.qh:16](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/_mod.qh#L16))
**Applicable decisions** · [ADR-0003](../../decisions/ADR-0003-source-generators.md) · [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md)

### 37. `common/gametypes/gametype/onslaught/sv_controlpoint.qc`

**Disposition** · architecture-replaced · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC links a separate icon, supplies absolute health/max/build count/owner capture on setup, suppresses status on that packet, and rounds normalized positive health upward on status. QC schedules its initial think for the next frame; native schedules build at its CP cadence and snapshots entity state.

**Open** · Check build phase timing, health rounding and the received icon state during joins.

**Sources** · [common/gametypes/gametype/onslaught/sv_controlpoint.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/sv_controlpoint.qc) · [common/gametypes/gametype/onslaught/sv_onslaught.qc:652](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/sv_onslaught.qc#L652) · [OnslaughtControlPoint.cs:532](../../../src/VortexArena.Common/Gameplay/GameTypes/OnslaughtControlPoint.cs#L532)
**Include routes** · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L17) → [common/gametypes/gametype/onslaught/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/_mod.inc#L7))
**Applicable decisions** · [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md)

### 38. `common/gametypes/gametype/onslaught/sv_controlpoint.qh`

**Disposition** · architecture-replaced · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC bounds are icon bounds, separate from the larger control-point trigger. Native icon completion switches solidity and Think; status/setup constants are private to QC.

**Open** · Compare solid footprint on completion and visibility update for building/destroyed icons.

**Sources** · [common/gametypes/gametype/onslaught/sv_controlpoint.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/sv_controlpoint.qh) · [common/gametypes/gametype/onslaught/sv_onslaught.qc:625](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/sv_onslaught.qc#L625) · [OnslaughtControlPoint.cs:555](../../../src/VortexArena.Common/Gameplay/GameTypes/OnslaughtControlPoint.cs#L555)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L5) → [server/chat.qc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/chat.qc#L3) → [common/gametypes/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.qh#L7) → [common/gametypes/gametype/_mod.qh:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.qh#L17) → [common/gametypes/gametype/onslaught/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/_mod.qh#L7))
**Applicable decisions** · [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md)

### 39. `common/gametypes/gametype/onslaught/sv_generator.qc`

**Disposition** · architecture-replaced · inventory `unresolved`.
**Evidence quality** · Static source comparison.

Generator setup and reset link the same sender. Unlike the CP sender, both QC sections can be present together. The native generator is a world entity and graph links have their own stream.

**Open** · Find paired setup/status presentation for damage, reset and spectator join, including normalized health rounding.

**Sources** · [common/gametypes/gametype/onslaught/sv_generator.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/sv_generator.qc) · [common/gametypes/gametype/onslaught/sv_onslaught.qc:1059](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/sv_onslaught.qc#L1059) · [ServerNet.cs:906](../../../game/net/ServerNet.cs#L906)
**Include routes** · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L17) → [common/gametypes/gametype/onslaught/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/_mod.inc#L14))
**Applicable decisions** · [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md)

### 40. `common/gametypes/gametype/onslaught/sv_generator.qh`

**Disposition** · architecture-replaced · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

The QC sender interface and setup/status flags accompany the same physical generator bounds as in native spawn setup.

**Open** · Compare shot traces against the built generator before validating any presentation behavior.

**Sources** · [common/gametypes/gametype/onslaught/sv_generator.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/sv_generator.qh) · [common/gametypes/gametype/onslaught/sv_onslaught.qc:1128](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/sv_onslaught.qc#L1128) · [OnslaughtControlPoint.cs:182](../../../src/VortexArena.Common/Gameplay/GameTypes/OnslaughtControlPoint.cs#L182)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L5) → [server/chat.qc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/chat.qc#L3) → [common/gametypes/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.qh#L7) → [common/gametypes/gametype/_mod.qh:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.qh#L17) → [common/gametypes/gametype/onslaught/_mod.qh:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/_mod.qh#L14))
**Applicable decisions** · [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md)

### 41. `common/gametypes/gametype/onslaught/sv_onslaught.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC registers teams and score limit, 0.2/1-second control-point/generator cadences, graph lists, shield/push settings and three HavocBot roles. Native creates typed graph nodes, builds the graph and computes attackability from links.

**Open** · Compare spawn-by and recent-death state, overtime feedback throttles and role assignment transitions.

**Sources** · [common/gametypes/gametype/onslaught/sv_onslaught.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/sv_onslaught.qh) · [common/gametypes/gametype/onslaught/sv_onslaught.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/sv_onslaught.qh#L7) · [Onslaught.cs:142](../../../src/VortexArena.Common/Gameplay/GameTypes/Onslaught.cs#L142)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L5) → [server/chat.qc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/chat.qc#L3) → [common/gametypes/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.qh#L7) → [common/gametypes/gametype/_mod.qh:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.qh#L17) → [common/gametypes/gametype/onslaught/_mod.qh:18](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/onslaught/_mod.qh#L18))
**Applicable decisions** · [ADR-0007](../../decisions/ADR-0007-entity-model.md)

### 42. `common/gametypes/gametype/race/race.qc`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC topics include the following. Qualification and starting-grid order, lap finish rule, and weapon interference only in the competitive phase. The native Guide now supplies the entry with the translated paragraph keys from the pinned callback.

**Open** · Check layout, translated placeholders and color runs in a non-English locale.

**Sources** · [common/gametypes/gametype/race/race.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/race/race.qc) · [menu/xonotic/dialog_media_guide.qc:77](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/dialog_media_guide.qc#L77) · [GuideCatalogText.cs:138](../../../game/menu/dialogs/GuideCatalogText.cs#L138)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:18](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L18) → [common/gametypes/gametype/race/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/race/_mod.inc#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:18](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L18) → [common/gametypes/gametype/race/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/race/_mod.inc#L2)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:18](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L18) → [common/gametypes/gametype/race/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/race/_mod.inc#L2))

### 43. `common/gametypes/gametype/race/sv_race.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

Static mode add sets race limits before initialization. Native has a dedicated initialization and activation selected by GameWorld.

**Open** · Verify qualifying-to-race limits and cleanup when changing away from Race.

**Sources** · [common/gametypes/gametype/race/sv_race.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/race/sv_race.qh) · [common/gametypes/gametype/race/sv_race.qh:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/race/sv_race.qh#L4) · [Race.cs:307](../../../src/VortexArena.Common/Gameplay/GameTypes/Race.cs#L307)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L5) → [server/chat.qc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/chat.qc#L3) → [common/gametypes/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.qh#L7) → [common/gametypes/gametype/_mod.qh:18](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.qh#L18) → [common/gametypes/gametype/race/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/race/_mod.qh#L7))
**Applicable decisions** · [ADR-0003](../../decisions/ADR-0003-source-generators.md)

### 44. `common/gametypes/gametype/survival/survival.qc`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC topics include the following. Hidden survivor identities, randomized asymmetric teams, grace period and survival-contingent frag/time bonuses. The same QC unit also registers linked role statuses; native ClientNet consumes recipient-specific objective state. The native Guide now supplies the entry with the translated paragraph keys from the pinned callback.

**Open** · Check layout, translated placeholders and color runs in a non-English locale.

**Sources** · [common/gametypes/gametype/survival/survival.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/survival/survival.qc) · [menu/xonotic/dialog_media_guide.qc:77](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/dialog_media_guide.qc#L77) · [GuideCatalogText.cs:145](../../../game/menu/dialogs/GuideCatalogText.cs#L145)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:19](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L19) → [common/gametypes/gametype/survival/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/survival/_mod.inc#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:19](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L19) → [common/gametypes/gametype/survival/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/survival/_mod.inc#L2)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:19](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L19) → [common/gametypes/gametype/survival/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/survival/_mod.inc#L2))

### 45. `common/gametypes/gametype/survival/sv_survival.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC stores round kills without exposing them until scoring. Native banks the signed delta and awards and resets the counter at round end.

**Open** · Check aborts, reconnect and observer disclosure independently of final score.

**Sources** · [common/gametypes/gametype/survival/sv_survival.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/survival/sv_survival.qh) · [common/gametypes/gametype/survival/sv_survival.qh:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/survival/sv_survival.qh#L5) · [Survival.cs:409](../../../src/VortexArena.Common/Gameplay/GameTypes/Survival.cs#L409)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L5) → [server/chat.qc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/chat.qc#L3) → [common/gametypes/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.qh#L7) → [common/gametypes/gametype/_mod.qh:19](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.qh#L19) → [common/gametypes/gametype/survival/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/survival/_mod.qh#L7))
**Applicable decisions** · [ADR-0007](../../decisions/ADR-0007-entity-model.md)

### 46. `common/gametypes/gametype/tdm/sv_tdm.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC registers static TDM and initializes using point/lead and team-spawn cvars. GameWorld activates a dedicated native mode with typed score handling.

**Open** · Test zero limits, per-map teams override and team spawn distribution.

**Sources** · [common/gametypes/gametype/tdm/sv_tdm.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/tdm/sv_tdm.qh) · [common/gametypes/gametype/tdm/sv_tdm.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/tdm/sv_tdm.qh#L7) · [Tdm.cs:122](../../../src/VortexArena.Common/Gameplay/GameTypes/Tdm.cs#L122)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L5) → [server/chat.qc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/chat.qc#L3) → [common/gametypes/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.qh#L7) → [common/gametypes/gametype/_mod.qh:20](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.qh#L20) → [common/gametypes/gametype/tdm/_mod.qh:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/tdm/_mod.qh#L4))
**Applicable decisions** · [ADR-0003](../../decisions/ADR-0003-source-generators.md)

### 47. `common/gametypes/gametype/tdm/tdm.qc`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC topics include the following. Team version of DM, frag/suicide/teamkill scoring and shared weapons and map-control advice. The native Guide now supplies the entry with the translated paragraph keys from the pinned callback.

**Open** · Check layout, translated placeholders and color runs in a non-English locale.

**Sources** · [common/gametypes/gametype/tdm/tdm.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/tdm/tdm.qc) · [menu/xonotic/dialog_media_guide.qc:77](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/dialog_media_guide.qc#L77) · [GuideCatalogText.cs:154](../../../game/menu/dialogs/GuideCatalogText.cs#L154)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:20](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L20) → [common/gametypes/gametype/tdm/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/tdm/_mod.inc#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:20](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L20) → [common/gametypes/gametype/tdm/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/tdm/_mod.inc#L2)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:20](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L20) → [common/gametypes/gametype/tdm/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/tdm/_mod.inc#L2))

### 48. `common/gametypes/gametype/tka/tka.qc`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC topics include the following. Team possession and the difference from FFA regarding who receives frag points. The native Guide now supplies the entry with the translated paragraph keys from the pinned callback.

**Open** · Check layout, translated placeholders and color runs in a non-English locale.

**Sources** · [common/gametypes/gametype/tka/tka.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/tka/tka.qc) · [menu/xonotic/dialog_media_guide.qc:77](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/dialog_media_guide.qc#L77) · [GuideCatalogText.cs:159](../../../game/menu/dialogs/GuideCatalogText.cs#L159)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:21](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L21) → [common/gametypes/gametype/tka/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/tka/_mod.inc#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:21](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L21) → [common/gametypes/gametype/tka/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/tka/_mod.inc#L2)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L53) → [common/gametypes/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.inc#L7) → [common/gametypes/gametype/_mod.inc:21](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.inc#L21) → [common/gametypes/gametype/tka/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/tka/_mod.inc#L2))

### 49. `common/gametypes/gametype/tmayhem/sv_tmayhem.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

The header intentionally exposes the team scoring scale, kill/damage weights and self-damage switch to the FFA compilation unit. Native resolves per-mode weight prefixes through shared Mayhem scoring.

**Open** · Compare negative or fractional damage, rounding order, and per-team versus individual accumulation.

**Sources** · [common/gametypes/gametype/tmayhem/sv_tmayhem.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/tmayhem/sv_tmayhem.qh) · [common/gametypes/gametype/mayhem/sv_mayhem.qc:145](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/mayhem/sv_mayhem.qc#L145) · [Mayhem.cs:65](../../../src/VortexArena.Common/Gameplay/GameTypes/Mayhem.cs#L65)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L5) → [server/chat.qc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/chat.qc#L3) → [common/gametypes/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/_mod.qh#L7) → [common/gametypes/gametype/_mod.qh:22](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/_mod.qh#L22) → [common/gametypes/gametype/tmayhem/_mod.qh:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/tmayhem/_mod.qh#L4))
**Applicable decisions** · [ADR-0018](../../decisions/ADR-0018-config-layer.md)

### 50. `common/mapobjects/platforms.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

Map spawn constructs center/outside activation geometry. Both versions use bit 0 for the low trigger and callbacks for travel and crushing.

**Open** · Measure phase, crush retry cooldown and linked activation under a blocked pusher.

**Sources** · [common/mapobjects/platforms.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/platforms.qh) · [common/mapobjects/platforms.qc:34](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/platforms.qc#L34) · [Platforms.cs:178](../../../src/VortexArena.Common/Gameplay/MapObjects/Platforms.cs#L178)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L26) → [common/mapobjects/_mod.qh:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L4)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L17) → [server/main.qc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/main.qc#L8) → [common/mapobjects/_mod.qh:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L4))

### 51. `common/mapobjects/func/train.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

Map spawn reads curve, turn and need-activation bits, then follows path corners. Native train registers a use callback and looks ahead to compute facing.

**Open** · Compare curve splines, waiting and activation resume at a shared path corner.

**Sources** · [common/mapobjects/func/train.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/func/train.qh) · [common/mapobjects/func/train.qc:19](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/func/train.qc#L19) · [MovingBrushes.cs:297](../../../src/VortexArena.Common/Gameplay/MapObjects/MovingBrushes.cs#L297)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L26) → [common/mapobjects/_mod.qh:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L9) → [common/mapobjects/func/_mod.qh:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/func/_mod.qh#L17)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L17) → [server/main.qc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/main.qc#L8) → [common/mapobjects/_mod.qh:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L9) → [common/mapobjects/func/_mod.qh:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/func/_mod.qh#L17))

### 52. `common/mapobjects/target/location.qh`

**Disposition** · architecture-replaced · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC creates the named location list at static init and spawn adds map labels. Native registers both target_location and info_location through TargetUtilities and stores location data on map entities.

**Open** · Compare nearest-location selection, tie ordering and map teardown.

**Sources** · [common/mapobjects/target/location.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/target/location.qh) · [common/mapobjects/target/location.qc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/target/location.qc#L5) · [TargetUtilities.cs:247](../../../src/VortexArena.Common/Gameplay/MapObjects/TargetUtilities.cs#L247)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L26) → [common/mapobjects/_mod.qh:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L11) → [common/mapobjects/target/_mod.qh:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/target/_mod.qh#L5)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L5) → [server/chat.qc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/chat.qc#L4))
**Applicable decisions** · [ADR-0007](../../decisions/ADR-0007-entity-model.md)

### 53. `common/mapobjects/target/music.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

The QC contract distinguishes default, explicitly selected and positional music and tracks state/lifetime. Native has separate target and trigger spawns feeding the music system.

**Open** · Compare server restore and kill, client fade/removal, origin-bit selection and transition priority.

**Sources** · [common/mapobjects/target/music.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/target/music.qh) · [common/mapobjects/target/music.qc:178](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/target/music.qc#L178) · [TargetMusic.cs:111](../../../src/VortexArena.Common/Gameplay/MapObjects/TargetMusic.cs#L111)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L12) → [client/view.qc:21](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/view.qc#L21)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:30](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L30) → [server/world.qc:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/world.qc#L10))

### 54. `common/mapobjects/target/speed.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

Map flags dispatch percentage/set/add, signed axes and launcher handling. Native TargetUtilities reads corresponding bits and resolves launcher axis conflicts.

**Open** · Check combinations with opposing signs, zero velocity, negative percentage and prediction.

**Sources** · [common/mapobjects/target/speed.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/target/speed.qh) · [common/mapobjects/target/speed.qc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/target/speed.qc#L9) · [TargetUtilities.cs:122](../../../src/VortexArena.Common/Gameplay/MapObjects/TargetUtilities.cs#L122)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L26) → [common/mapobjects/_mod.qh:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L11) → [common/mapobjects/target/_mod.qh:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/target/_mod.qh#L10)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L17) → [server/main.qc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/main.qc#L8) → [common/mapobjects/_mod.qh:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L11) → [common/mapobjects/target/_mod.qh:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/target/_mod.qh#L10))

### 55. `common/mapobjects/trigger/counter.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

Both paths handle fire-at-count with bit 2, with bit 3 selecting per-player count. QC tracks global instances for reset while native registers map callbacks.

**Open** · Verify mixed-player countdown, resets during round change and target firing order.

**Sources** · [common/mapobjects/trigger/counter.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/counter.qh) · [common/mapobjects/trigger/counter.qc:36](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/counter.qc#L36) · [Triggers.cs:586](../../../src/VortexArena.Common/Gameplay/MapObjects/Triggers.cs#L586)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L26) → [common/mapobjects/_mod.qh:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L12) → [common/mapobjects/trigger/_mod.qh:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/_mod.qh#L2)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L7) → [server/client.qc:18](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/client.qc#L18))

### 56. `common/mapobjects/trigger/gravity.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

Bit 0 retains gravity outside and bit 1 controls initial enablement. QC touch and native trigger touch use the sticky branch to decide whether to restore.

**Open** · Compare overlapping triggers, re-enable latency and preservation of nondefault prior gravity.

**Sources** · [common/mapobjects/trigger/gravity.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/gravity.qh) · [common/mapobjects/trigger/gravity.qc:53](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/gravity.qc#L53) · [Triggers.cs:474](../../../src/VortexArena.Common/Gameplay/MapObjects/Triggers.cs#L474)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L26) → [common/mapobjects/_mod.qh:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L12) → [common/mapobjects/trigger/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/_mod.qh#L7)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L17) → [server/main.qc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/main.qc#L8) → [common/mapobjects/_mod.qh:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L12) → [common/mapobjects/trigger/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/_mod.qh#L7))

### 57. `common/mapobjects/trigger/heal.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

Both interpret bit 2 as playing the sample even if the touch cannot heal. Native wires trigger and target variants separately.

**Open** · Compare no-op heal, health caps, interval and targeted activation.

**Sources** · [common/mapobjects/trigger/heal.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/heal.qh) · [common/mapobjects/trigger/heal.qc:20](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/heal.qc#L20) · [Triggers.cs:412](../../../src/VortexArena.Common/Gameplay/MapObjects/Triggers.cs#L412)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L26) → [common/mapobjects/_mod.qh:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L12) → [common/mapobjects/trigger/_mod.qh:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/_mod.qh#L8)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L17) → [server/main.qc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/main.qc#L8) → [common/mapobjects/_mod.qh:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L12) → [common/mapobjects/trigger/_mod.qh:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/_mod.qh#L8))

### 58. `common/mapobjects/trigger/hurt.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Spawnflag and touch regressions](S1-VERIFICATION.md#q3-trigger_hurt)

Native and QC use bit 4 for the slower Quake III creature cadence. Both gate non-creatures on explicit trigger-damage eligibility. CTF flags and KeyHunt keys capture the return-when-unreachable cvar on spawn. The native map trigger no longer treats every pushable as a target.

**Open** · Compare bot hazard sweep boundaries and brush containment on rebuilt geometry.

**Sources** · [common/mapobjects/trigger/hurt.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/hurt.qh) · [common/mapobjects/trigger/hurt.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/hurt.qc#L26) · [Triggers.cs:292](../../../src/VortexArena.Common/Gameplay/MapObjects/Triggers.cs#L292)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L26) → [common/mapobjects/_mod.qh:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L12) → [common/mapobjects/trigger/_mod.qh:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/_mod.qh#L9)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:30](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L30) → [server/world.qc:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/world.qc#L11))

### 59. `common/mapobjects/trigger/impulse.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC defines falloffs 0/1/2, three force defaults, directional target mode and a maximum 0.15 timestep. Native branches from impulse setup into radial, directional or accelerating paths and evaluates the corresponding falloffs.

**Open** · Check server multiplier and power cvars, long-gap clamp versus skipped touch, and acceleration cap at high incoming speed.

**Sources** · [common/mapobjects/trigger/impulse.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/impulse.qh) · [common/mapobjects/trigger/impulse.qc:29](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/impulse.qc#L29) · [Triggers.cs:874](../../../src/VortexArena.Common/Gameplay/MapObjects/Triggers.cs#L874)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L26) → [common/mapobjects/_mod.qh:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L12) → [common/mapobjects/trigger/_mod.qh:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/_mod.qh#L10)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L17) → [server/main.qc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/main.qc#L8) → [common/mapobjects/_mod.qh:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L12) → [common/mapobjects/trigger/_mod.qh:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/_mod.qh#L10))

### 60. `common/mapobjects/trigger/jumppads.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC distinguishes ballistic arcs, Q3 target_push and seven velocity-mode flags, and keeps three recently used pads. The native push, velocity and target spawns share a dispatcher with a predicted arm.

**Open** · Verify negative ceiling arcs, recent-pad frag attribution, item obstruction test and all flag combinations.

**Sources** · [common/mapobjects/trigger/jumppads.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/jumppads.qh) · [common/mapobjects/trigger/jumppads.qc:32](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/jumppads.qc#L32) · [Jumppads.cs:184](../../../src/VortexArena.Common/Gameplay/MapObjects/Jumppads.cs#L184)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L26) → [common/mapobjects/_mod.qh:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L12) → [common/mapobjects/trigger/_mod.qh:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/_mod.qh#L11)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L17) → [server/main.qc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/main.qc#L8) → [common/mapobjects/_mod.qh:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L12) → [common/mapobjects/trigger/_mod.qh:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/_mod.qh#L11))

### 61. `common/mapobjects/trigger/magicear.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

Tuba.NoteOff records the completed note and dispatches the empty-message path to registered map ears. Each slot is checked for melody, pitch, instrument and tempo before firing the target. The prior absence finding came from a stale source comment.

**Open** · Compare a complete melody, chat replacement chaining and invalid tells in a paired map fixture.

**Sources** · [common/mapobjects/trigger/magicear.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/magicear.qh) · [common/mapobjects/trigger/magicear.qc:28](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/magicear.qc#L28) · [Tuba.cs:290](../../../src/VortexArena.Common/Gameplay/Weapons/Tuba.cs#L290)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L26) → [common/mapobjects/_mod.qh:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L12) → [common/mapobjects/trigger/_mod.qh:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/_mod.qh#L13)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L17) → [server/main.qc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/main.qc#L8) → [common/mapobjects/_mod.qh:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L12) → [common/mapobjects/trigger/_mod.qh:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/_mod.qh#L13))

### 62. `common/mapobjects/trigger/monoflop.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC flag 0 chooses fixed pulse vs retriggerable timer. Native installs the corresponding use delegate at spawn.

**Open** · Verify multiple activates in one tick and target order when resetting.

**Sources** · [common/mapobjects/trigger/monoflop.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/monoflop.qh) · [common/mapobjects/trigger/monoflop.qc:41](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/monoflop.qc#L41) · [LogicGates.cs:97](../../../src/VortexArena.Common/Gameplay/MapObjects/LogicGates.cs#L97)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L26) → [common/mapobjects/_mod.qh:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L12) → [common/mapobjects/trigger/_mod.qh:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/_mod.qh#L14)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L17) → [server/main.qc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/main.qc#L8) → [common/mapobjects/_mod.qh:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L12) → [common/mapobjects/trigger/_mod.qh:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/_mod.qh#L14))

### 63. `common/mapobjects/trigger/relay_if.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

Map flag 0 inverts the tested active-state condition before forwarding. Native uses a registered relay_if use handler.

**Open** · Compare missing targets, mixed active states and inversion with multiple destinations.

**Sources** · [common/mapobjects/trigger/relay_if.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/relay_if.qh) · [common/mapobjects/trigger/relay_if.qc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/relay_if.qc#L13) · [LogicGates.cs:281](../../../src/VortexArena.Common/Gameplay/MapObjects/LogicGates.cs#L281)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L26) → [common/mapobjects/_mod.qh:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L12) → [common/mapobjects/trigger/_mod.qh:19](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/_mod.qh#L19)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L17) → [server/main.qc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/main.qc#L8) → [common/mapobjects/_mod.qh:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L12) → [common/mapobjects/trigger/_mod.qh:19](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/_mod.qh#L19))

### 64. `common/mapobjects/trigger/relay_teamcheck.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC uses bit 0 for the non-teamgame case and bit 1 to invert. Native branches on the former independently and uses the same flag layout.

**Open** · Test a world activator and unset team, plus grouped target links.

**Sources** · [common/mapobjects/trigger/relay_teamcheck.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/relay_teamcheck.qh) · [common/mapobjects/trigger/relay_teamcheck.qc:24](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/relay_teamcheck.qc#L24) · [LogicGates.cs:362](../../../src/VortexArena.Common/Gameplay/MapObjects/LogicGates.cs#L362)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L26) → [common/mapobjects/_mod.qh:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L12) → [common/mapobjects/trigger/_mod.qh:20](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/_mod.qh#L20)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L17) → [server/main.qc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/main.qc#L8) → [common/mapobjects/_mod.qh:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L12) → [common/mapobjects/trigger/_mod.qh:20](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/_mod.qh#L20))

### 65. `common/mapobjects/trigger/swamp.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC maintains a linked set and a slug for leave detection, with a damage interval and slowdown factor. Native registers touch and cleanup around typed swamp state.

**Open** · Compare stale contacts, overlap priorities, and a factor greater than one.

**Sources** · [common/mapobjects/trigger/swamp.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/swamp.qh) · [common/mapobjects/trigger/swamp.qc:59](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/swamp.qc#L59) · [Triggers.cs:773](../../../src/VortexArena.Common/Gameplay/MapObjects/Triggers.cs#L773)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L26) → [common/mapobjects/_mod.qh:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L12) → [common/mapobjects/trigger/_mod.qh:22](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/_mod.qh#L22)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L7) → [server/client.qc:20](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/client.qc#L20))

### 66. `common/mapobjects/trigger/teleport.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC distinguishes observer-only bit 0 and keep-speed bit 1 on trigger_teleport, plus keep-speed bit 0 on target_teleporter. Native maps them per classname and gates the speed clamp independently of direction.

**Open** · Compare spectator touch, target use and predicted angle/velocity with the same initial pose.

**Sources** · [common/mapobjects/trigger/teleport.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/teleport.qh) · [common/mapobjects/trigger/teleport.qc:43](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/teleport.qc#L43) · [Teleporters.cs:29](../../../src/VortexArena.Common/Gameplay/MapObjects/Teleporters.cs#L29)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L26) → [common/mapobjects/_mod.qh:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L12) → [common/mapobjects/trigger/_mod.qh:23](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/_mod.qh#L23)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L17) → [server/main.qc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/main.qc#L8) → [common/mapobjects/_mod.qh:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/_mod.qh#L12) → [common/mapobjects/trigger/_mod.qh:23](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/_mod.qh#L23))

### 67. `common/mapobjects/trigger/viewloc.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Rail entry and exit on server-only replacement entities](../view-location/README.md)

Native resolves the two authored targets, replicates the camera state and implements track, pitch, hard-snap and close-snap rules. The original reserves bit 0.

**Open** · Two remote clients exercised combined flags, exit and reentry on a server-only entity replacement. Exact cross-snapshot timing remains open.

**Sources** · [common/mapobjects/trigger/viewloc.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mapobjects/trigger/viewloc.qh) · [common/viewloc.qc:130](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/viewloc.qc#L130) · [ViewLocationState.cs:70](../../../src/VortexArena.Common/Gameplay/MapObjects/ViewLocationState.cs#L70)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L12) → [client/view.qc:22](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/view.qc#L22)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L26) → [common/physics/all.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/physics/all.inc#L1) → [common/physics/player.qc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/physics/player.qc#L14))

### 68. `common/monsters/all.qc`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

Base prefixes the monster model directory and offers a MonsterModel hook. The pinned QC tree contains the hook declaration and dispatch with no registered handler. Stock native monsters use the resulting model paths.

**Open** · Custom mutator overrides and per-model render or cache behavior require independent evidence.

**Sources** · [common/monsters/all.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/monsters/all.qc) · [common/monsters/monster/mage.qh:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/monsters/monster/mage.qh#L6) · [Mage.cs:56](../../../src/VortexArena.Common/Gameplay/Monsters/Mage.cs#L56)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:48](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L48) → [common/monsters/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/monsters/_mod.inc#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:48](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L48) → [common/monsters/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/monsters/_mod.inc#L2)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:48](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L48) → [common/monsters/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/monsters/_mod.inc#L2))

### 69. `common/monsters/all.qh`

**Disposition** · architecture-replaced · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC builds a five-bit registry with a hidden null entry and fallback lookup. Native uses typed generated registration for monster definitions and name/classname lookup for spawning.

**Open** · Check unknown and null lookup behavior and guide visibility independently of ID numbering.

**Sources** · [common/monsters/all.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/monsters/all.qh) · [common/monsters/all.qh:18](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/monsters/all.qh#L18) · [Registry.cs:22](../../../src/VortexArena.Common/Framework/Registry.cs#L22)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L7) → [common/animdecide.qc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/animdecide.qc#L4) → [common/monsters/_mod.qh:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/monsters/_mod.qh#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:48](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L48) → [common/monsters/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/monsters/_mod.inc#L2) → [common/monsters/all.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/monsters/all.qc#L1)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L3) → [server/antilag.qc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/antilag.qc#L3) → [common/monsters/sv_monsters.qh:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/monsters/sv_monsters.qh#L3))
**Applicable decisions** · [ADR-0003](../../decisions/ADR-0003-source-generators.md)

### 70. `common/monsters/monster.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC definitions expose setup, alive/dead Think, death, pain, animation and hidden flags. Native spawns the definition into a MonsterState and dispatches damage, think and model state through typed hooks.

**Open** · Trace passive, undead, Quake-resizing and unused ride/crush flags per monster.

**Sources** · [common/monsters/monster.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/monsters/monster.qh) · [common/monsters/monster.qh:82](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/monsters/monster.qh#L82) · [MonsterAI.cs:228](../../../src/VortexArena.Common/Gameplay/Monsters/MonsterAI.cs#L228)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L7) → [common/animdecide.qc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/animdecide.qc#L4) → [common/monsters/_mod.qh:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/monsters/_mod.qh#L2) → [common/monsters/all.qh:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/monsters/all.qh#L13)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:48](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L48) → [common/monsters/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/monsters/_mod.inc#L2) → [common/monsters/all.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/monsters/all.qc#L1) → [common/monsters/all.qh:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/monsters/all.qh#L13)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L2) → [server/anticheat.qc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/anticheat.qc#L9) → [server/command/common.qh:31](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/command/common.qh#L31) → [common/monsters/sv_spawn.qh:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/monsters/sv_spawn.qh#L3))

### 71. `common/monsters/sv_monsters.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

Both code paths use the same scalar skill formula and maintain explicit targeting/movement state. QC header also supplies spawn gates, health bars, sound categories and global lists.

**Open** · Compare max-per-player, edit command, delayed attack and respawn flag interactions.

**Sources** · [common/monsters/sv_monsters.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/monsters/sv_monsters.qh) · [common/monsters/sv_monsters.qh:63](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/monsters/sv_monsters.qh#L63) · [MonsterAI.cs:737](../../../src/VortexArena.Common/Gameplay/Monsters/MonsterAI.cs#L737)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L3) → [server/antilag.qc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/antilag.qc#L3))

### 72. `common/mutators/mutator/damagetext/cl_damagetext.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC decodes linked damage events and settings drive accumulation, exposure, friendly-fire and world/screen motion. Native reads lifetime and accumulation cvars before drawing received events.

**Open** · Compare selected recipients, verbose formatting, 2D overlap and negative lifetime under rapid hits.

**Sources** · [common/mutators/mutator/damagetext/cl_damagetext.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/damagetext/cl_damagetext.qh) · [common/mutators/mutator/damagetext/cl_damagetext.qc:219](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/damagetext/cl_damagetext.qc#L219) · [DamageTextConfig.cs:62](../../../game/client/DamageTextConfig.cs#L62)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L9) → [common/mutators/mutator/damagetext/_mod.inc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/damagetext/_mod.inc#L4) → [common/mutators/mutator/damagetext/cl_damagetext.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/damagetext/cl_damagetext.qc#L1))

### 73. `common/mutators/mutator/damagetext/ui_damagetext.qh`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

Game settings registers the tab factory. Native Game settings constructs its Damage text tab with the same width, rows and columns, so the legacy configuration-only comparison omitted an actual UI entry.

**Open** · Check show notification, live preview and per-control binding in the menu.

**Sources** · [common/mutators/mutator/damagetext/ui_damagetext.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/damagetext/ui_damagetext.qh) · [menu/gamesettings.qh:25](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/gamesettings.qh#L25) · [DialogSettingsGame.cs:788](../../../game/menu/dialogs/DialogSettingsGame.cs#L788)
**Include routes** · MENUQC ([menu/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L5) → [menu/_mod.inc:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/_mod.inc#L11) → [menu/xonotic/_mod.inc:71](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/_mod.inc#L71) → [menu/xonotic/dialog_settings_game.qc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/dialog_settings_game.qc#L3) → [menu/gamesettings.qh:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/gamesettings.qh#L9))

### 74. `common/mutators/mutator/dodging/cl_dodging.qh`

**Disposition** · architecture-replaced · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

The client header exposes the timeout and opt-in var while prediction reads its replicated dodge stat. Native uses typed per-client options and hooks around Player input.

**Open** · Verify join, live client change and prediction before the host receives the changed options.

**Sources** · [common/mutators/mutator/dodging/cl_dodging.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/dodging/cl_dodging.qh) · [common/mutators/mutator/dodging/sv_dodging.qc:47](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/dodging/sv_dodging.qc#L47) · [DodgingMutator.cs:87](../../../src/VortexArena.Common/Gameplay/Mutators/DodgingMutator.cs#L87)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L10) → [common/mutators/mutator/dodging/_mod.inc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/dodging/_mod.inc#L4) → [common/mutators/mutator/dodging/cl_dodging.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/dodging/cl_dodging.qc#L1))
**Applicable decisions** · [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md)

### 75. `common/mutators/mutator/dodging/dodging.qc`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC topics include the following. Double-tap dodge from ground or a wall and Overkill enabling the mutator. The native Guide now supplies the entry with the translated paragraph keys from the pinned callback.

**Open** · Check layout, translated placeholders and color runs in a non-English locale.

**Sources** · [common/mutators/mutator/dodging/dodging.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/dodging/dodging.qc) · [menu/xonotic/dialog_media_guide.qc:77](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/dialog_media_guide.qc#L77) · [GuideCatalogText.cs:203](../../../game/menu/dialogs/GuideCatalogText.cs#L203)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L10) → [common/mutators/mutator/dodging/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/dodging/_mod.inc#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L10) → [common/mutators/mutator/dodging/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/dodging/_mod.inc#L2)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L10) → [common/mutators/mutator/dodging/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/dodging/_mod.inc#L2))

### 76. `common/mutators/mutator/dodging/dodging.qh`

**Disposition** · architecture-replaced · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC registers the mutator for the Guide. The native equivalent supplies its display name and description from the stock callback.

**Open** · Compare display localization and settings activation independently.

**Sources** · [common/mutators/mutator/dodging/dodging.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/dodging/dodging.qh) · [common/mutators/mutator/dodging/dodging.qh:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/dodging/dodging.qh#L8) · [DialogMediaGuide.cs:205](../../../game/menu/dialogs/DialogMediaGuide.cs#L205)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L10) → [common/mutators/mutator/dodging/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/dodging/_mod.inc#L2) → [common/mutators/mutator/dodging/dodging.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/dodging/dodging.qc#L1)) · MENUQC ([menu/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L5) → [menu/_mod.inc:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/_mod.inc#L11) → [menu/xonotic/_mod.inc:132](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/_mod.inc#L132) → [menu/xonotic/guide/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/guide/_mod.inc#L5) → [menu/xonotic/guide/pages.qc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/guide/pages.qc#L9)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L10) → [common/mutators/mutator/dodging/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/dodging/_mod.inc#L2) → [common/mutators/mutator/dodging/dodging.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/dodging/dodging.qc#L1))
**Applicable decisions** · [ADR-0003](../../decisions/ADR-0003-source-generators.md)

### 77. `common/mutators/mutator/dodging/sv_dodging.qh`

**Disposition** · architecture-replaced · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC authority reads both values from the dodging client's replicated cvar storage. Native provides per-client timeout/opt-in delegates instead of the QC field macros.

**Open** · Verify actual relay and disconnection fallback for different clients.

**Sources** · [common/mutators/mutator/dodging/sv_dodging.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/dodging/sv_dodging.qh) · [common/mutators/mutator/dodging/sv_dodging.qc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/dodging/sv_dodging.qc#L52) · [DodgingMutator.cs:79](../../../src/VortexArena.Common/Gameplay/Mutators/DodgingMutator.cs#L79)
**Include routes** · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L10) → [common/mutators/mutator/dodging/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/dodging/_mod.inc#L7) → [common/mutators/mutator/dodging/sv_dodging.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/dodging/sv_dodging.qc#L1))
**Applicable decisions** · [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md)

### 78. `common/mutators/mutator/instagib/instagib.qc`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC topics include the following. Vaporizer-only combat, ten-second ammo starvation, extra lives and movement powerups. The native Guide now supplies the entry with the translated paragraph keys from the pinned callback.

**Open** · Check layout, translated placeholders and color runs in a non-English locale.

**Sources** · [common/mutators/mutator/instagib/instagib.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/instagib/instagib.qc) · [menu/xonotic/dialog_media_guide.qc:77](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/dialog_media_guide.qc#L77) · [GuideCatalogText.cs:218](../../../game/menu/dialogs/GuideCatalogText.cs#L218)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L15) → [common/mutators/mutator/instagib/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/instagib/_mod.inc#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L15) → [common/mutators/mutator/instagib/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/instagib/_mod.inc#L2)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L15) → [common/mutators/mutator/instagib/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/instagib/_mod.inc#L2))

### 79. `common/mutators/mutator/instagib/instagib.qh`

**Disposition** · architecture-replaced · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC registers the mutator for the Guide. The native equivalent supplies its display name and description from the stock callback.

**Open** · Check localized case and whether other native menus expose equivalent standalone toggles.

**Sources** · [common/mutators/mutator/instagib/instagib.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/instagib/instagib.qh) · [common/mutators/mutator/instagib/instagib.qh:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/instagib/instagib.qh#L8) · [DialogMediaGuide.cs:205](../../../game/menu/dialogs/DialogMediaGuide.cs#L205)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L15) → [common/mutators/mutator/instagib/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/instagib/_mod.inc#L2) → [common/mutators/mutator/instagib/instagib.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/instagib/instagib.qc#L1)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:47](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L47) → [common/weapons/_all.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/_all.inc#L2) → [common/weapons/all.qc:20](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.qc#L20) → [common/weapons/weapon/_mod.inc:19](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/weapon/_mod.inc#L19) → [common/weapons/weapon/vaporizer.qc:431](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/weapon/vaporizer.qc#L431)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L15) → [common/mutators/mutator/instagib/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/instagib/_mod.inc#L2) → [common/mutators/mutator/instagib/instagib.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/instagib/instagib.qc#L1))
**Applicable decisions** · [ADR-0003](../../decisions/ADR-0003-source-generators.md)

### 80. `common/mutators/mutator/nix/nix.qc`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC topics include the following. Shared randomized weapon, timed rotation, and removal of weapon/ammo pickups. The native Guide now supplies the entry with the translated paragraph keys from the pinned callback.

**Open** · Check layout, translated placeholders and color runs in a non-English locale.

**Sources** · [common/mutators/mutator/nix/nix.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/nix/nix.qc) · [menu/xonotic/dialog_media_guide.qc:77](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/dialog_media_guide.qc#L77) · [GuideCatalogText.cs:248](../../../game/menu/dialogs/GuideCatalogText.cs#L248)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:24](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L24) → [common/mutators/mutator/nix/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/nix/_mod.inc#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:24](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L24) → [common/mutators/mutator/nix/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/nix/_mod.inc#L2)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:24](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L24) → [common/mutators/mutator/nix/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/nix/_mod.inc#L2))

### 81. `common/mutators/mutator/nix/nix.qh`

**Disposition** · architecture-replaced · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC registers the mutator for the Guide. The native equivalent supplies its display name and description from the stock callback.

**Open** · Compare UI availability and naming separately from weapon rotation.

**Sources** · [common/mutators/mutator/nix/nix.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/nix/nix.qh) · [common/mutators/mutator/nix/nix.qh:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/nix/nix.qh#L8) · [DialogMediaGuide.cs:205](../../../game/menu/dialogs/DialogMediaGuide.cs#L205)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:24](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L24) → [common/mutators/mutator/nix/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/nix/_mod.inc#L2) → [common/mutators/mutator/nix/nix.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/nix/nix.qc#L1)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:24](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L24) → [common/mutators/mutator/nix/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/nix/_mod.inc#L2) → [common/mutators/mutator/nix/nix.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/nix/nix.qc#L1)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:24](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L24) → [common/mutators/mutator/nix/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/nix/_mod.inc#L2) → [common/mutators/mutator/nix/nix.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/nix/nix.qc#L1))
**Applicable decisions** · [ADR-0003](../../decisions/ADR-0003-source-generators.md)

### 82. `common/mutators/mutator/overkill/overkill.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC hides standalone Overkill and the native Guide likewise omits the standalone entry.

**Open** · Trace Create Game visibility and ensure manual enable behavior agrees with the ruleset and editor.

**Sources** · [common/mutators/mutator/overkill/overkill.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/overkill/overkill.qh) · [common/mutators/mutator/overkill/overkill.qh:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/overkill/overkill.qh#L8) · [DialogMediaGuide.cs:205](../../../game/menu/dialogs/DialogMediaGuide.cs#L205)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L26) → [common/mutators/mutator/overkill/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/overkill/_mod.inc#L7) → [common/mutators/mutator/overkill/overkill.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/overkill/overkill.qc#L1)) · MENUQC ([menu/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L5) → [menu/_mod.inc:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/_mod.inc#L11) → [menu/xonotic/_mod.inc:132](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/_mod.inc#L132) → [menu/xonotic/guide/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/guide/_mod.inc#L5) → [menu/xonotic/guide/pages.qc:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/guide/pages.qc#L11)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L26) → [common/mutators/mutator/overkill/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/overkill/_mod.inc#L7) → [common/mutators/mutator/overkill/overkill.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/overkill/overkill.qc#L1))

### 83. `common/mutators/mutator/powerups/sv_powerups.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC registers an always-present mutator, reads enable/drop/stack controls and tracks strength sound timestamps. Native hooks gate pickup and timed drops.

**Open** · Verify auto/manual drop, same-powerup stacks, Q3 count and timestamp update order.

**Sources** · [common/mutators/mutator/powerups/sv_powerups.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/powerups/sv_powerups.qh) · [common/mutators/mutator/powerups/sv_powerups.qc:80](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/powerups/sv_powerups.qc#L80) · [PowerupsMutator.cs:61](../../../src/VortexArena.Common/Gameplay/Mutators/PowerupsMutator.cs#L61)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:30](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L30) → [server/world.qc:16](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/world.qc#L16) → [common/mutators/mutator/powerups/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/powerups/_mod.qh#L7))

### 84. `common/mutators/mutator/powerups/powerup/invisibility.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

Both classnames map to Invisibility. QC initializes duration from an already-populated value or count before its balance fallback and registers skin 12 and a status icon.

**Open** · Compare count overrides, disable/re-enable, model skin and player alpha replication.

**Sources** · [common/mutators/mutator/powerups/powerup/invisibility.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/powerups/powerup/invisibility.qh) · [common/mutators/mutator/powerups/powerup/invisibility.qh:56](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/powerups/powerup/invisibility.qh#L56) · [ItemSpawnFuncs.cs:79](../../../src/VortexArena.Common/Gameplay/Items/ItemSpawnFuncs.cs#L79)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L12) → [client/view.qc:25](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/view.qc#L25) → [common/mutators/mutator/powerups/_mod.qh:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/powerups/_mod.qh#L10) → [common/mutators/mutator/powerups/powerup/_mod.qh:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/powerups/powerup/_mod.qh#L3)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L8) → [common/mutators/mutator/cloaked/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/cloaked/_mod.inc#L2) → [common/mutators/mutator/cloaked/cloaked.qc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/cloaked/cloaked.qc#L4)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:30](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L30) → [server/world.qc:16](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/world.qc#L16) → [common/mutators/mutator/powerups/_mod.qh:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/powerups/_mod.qh#L10) → [common/mutators/mutator/powerups/powerup/_mod.qh:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/powerups/powerup/_mod.qh#L3))

### 85. `common/mutators/mutator/powerups/powerup/shield.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

Both shield aliases resolve under the invincible internal name. QC supplies separate damage and force multipliers plus item count duration and sound/waypoint metadata.

**Open** · Compare damage rounding versus knockback, existing-duration precedence and pickup feedback.

**Sources** · [common/mutators/mutator/powerups/powerup/shield.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/powerups/powerup/shield.qh) · [common/mutators/mutator/powerups/powerup/shield.qh:54](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/powerups/powerup/shield.qh#L54) · [ItemSpawnFuncs.cs:77](../../../src/VortexArena.Common/Gameplay/Items/ItemSpawnFuncs.cs#L77)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L12) → [client/view.qc:25](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/view.qc#L25) → [common/mutators/mutator/powerups/_mod.qh:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/powerups/_mod.qh#L10) → [common/mutators/mutator/powerups/powerup/_mod.qh:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/powerups/powerup/_mod.qh#L5)) · MENUQC ([menu/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L5) → [menu/_mod.inc:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/_mod.inc#L11) → [menu/xonotic/_mod.inc:132](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/_mod.inc#L132) → [menu/xonotic/guide/_mod.inc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/guide/_mod.inc#L4) → [menu/xonotic/guide/guide.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/guide/guide.qc#L1) → [menu/xonotic/guide/guide.qh:96](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/guide/guide.qh#L96) → [common/mutators/mutator/powerups/powerup/_mod.qh:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/powerups/powerup/_mod.qh#L5)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:30](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L30) → [server/world.qc:16](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/world.qc#L16) → [common/mutators/mutator/powerups/_mod.qh:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/powerups/_mod.qh#L10) → [common/mutators/mutator/powerups/powerup/_mod.qh:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/powerups/powerup/_mod.qh#L5))

### 86. `common/mutators/mutator/powerups/powerup/speed.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

Both Speed classnames resolve to a powerup. QC supplies steam-independent pickup sound and a buff-model skin 9, plus configurable high speed and attack-time factor.

**Open** · Compare shot refire, movement and player model feedback for a count-overridden duration.

**Sources** · [common/mutators/mutator/powerups/powerup/speed.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/powerups/powerup/speed.qh) · [common/mutators/mutator/powerups/powerup/speed.qh:57](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/powerups/powerup/speed.qh#L57) · [ItemSpawnFuncs.cs:78](../../../src/VortexArena.Common/Gameplay/Items/ItemSpawnFuncs.cs#L78)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L12) → [client/view.qc:25](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/view.qc#L25) → [common/mutators/mutator/powerups/_mod.qh:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/powerups/_mod.qh#L10) → [common/mutators/mutator/powerups/powerup/_mod.qh:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/powerups/powerup/_mod.qh#L6)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L15) → [common/mutators/mutator/instagib/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/instagib/_mod.inc#L2) → [common/mutators/mutator/instagib/instagib.qc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/instagib/instagib.qc#L5)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:30](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L30) → [server/world.qc:16](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/world.qc#L16) → [common/mutators/mutator/powerups/_mod.qh:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/powerups/_mod.qh#L10) → [common/mutators/mutator/powerups/powerup/_mod.qh:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/powerups/powerup/_mod.qh#L6))

### 87. `common/mutators/mutator/rocketflying/rocketflying.qh`

**Disposition** · architecture-replaced · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC header is a MENUQC display entry. Native registered class addresses the runtime effect but does not establish localized display-name and availability parity.

**Open** · Trace menu controls and ruleset activation independently of the projectile hooks.

**Sources** · [common/mutators/mutator/rocketflying/rocketflying.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/rocketflying/rocketflying.qh) · [common/mutators/mutator/rocketflying/rocketflying.qh:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/rocketflying/rocketflying.qh#L8) · [RocketFlyingMutator.cs:30](../../../src/VortexArena.Common/Gameplay/Mutators/RocketFlyingMutator.cs#L30)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:32](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L32) → [common/mutators/mutator/rocketflying/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/rocketflying/_mod.inc#L2) → [common/mutators/mutator/rocketflying/rocketflying.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/rocketflying/rocketflying.qc#L1)) · MENUQC ([menu/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L5) → [menu/_mod.inc:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/_mod.inc#L11) → [menu/xonotic/_mod.inc:132](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/_mod.inc#L132) → [menu/xonotic/guide/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/guide/_mod.inc#L5) → [menu/xonotic/guide/pages.qc:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/guide/pages.qc#L17)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:32](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L32) → [common/mutators/mutator/rocketflying/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/rocketflying/_mod.inc#L2) → [common/mutators/mutator/rocketflying/rocketflying.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/rocketflying/rocketflying.qc#L1))
**Applicable decisions** · [ADR-0003](../../decisions/ADR-0003-source-generators.md)

### 88. `common/mutators/mutator/spawn_near_teammate/spawn_near_teammate.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

The server now checks the spawning player's preference in scoring and physical placement. Native client settings send this on connection and after a change. Two-caller tests exercise modes 0, 1 and 2, updates and disconnect.

**Open** · Compare two actual clients changing preferences mid-match.

**Sources** · [common/mutators/mutator/spawn_near_teammate/spawn_near_teammate.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/spawn_near_teammate/spawn_near_teammate.qh) · [common/mutators/mutator/spawn_near_teammate/sv_spawn_near_teammate.qc:30](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/spawn_near_teammate/sv_spawn_near_teammate.qc#L30) · [SpawnNearTeammateMutator.cs:70](../../../src/VortexArena.Common/Gameplay/Mutators/SpawnNearTeammateMutator.cs#L70)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:36](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L36) → [common/mutators/mutator/spawn_near_teammate/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/spawn_near_teammate/_mod.inc#L2) → [common/mutators/mutator/spawn_near_teammate/spawn_near_teammate.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/spawn_near_teammate/spawn_near_teammate.qc#L1)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:36](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L36) → [common/mutators/mutator/spawn_near_teammate/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/spawn_near_teammate/_mod.inc#L2) → [common/mutators/mutator/spawn_near_teammate/spawn_near_teammate.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/spawn_near_teammate/spawn_near_teammate.qc#L1)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:36](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L36) → [common/mutators/mutator/spawn_near_teammate/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/spawn_near_teammate/_mod.inc#L2) → [common/mutators/mutator/spawn_near_teammate/spawn_near_teammate.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/spawn_near_teammate/spawn_near_teammate.qc#L1))
**Applicable decisions** · [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md)

### 89. `common/mutators/mutator/status_effects/status_effect/burning.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC hidden Burning sets color, 10-second default and steam removal sound. Native registers the effect and runs damage, persistence in lava and clear-on-water/frozen paths.

**Open** · Compare default versus explicit lifetime, extinguish sound and status replication.

**Sources** · [common/mutators/mutator/status_effects/status_effect/burning.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/burning.qh) · [common/mutators/mutator/status_effects/status_effect/burning.qc:20](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/burning.qc#L20) · [StatusEffects.cs:169](../../../src/VortexArena.Common/Gameplay/StatusEffects.cs#L169)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L12) → [client/view.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/view.qc#L26) → [common/mutators/mutator/status_effects/_mod.qh:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/_mod.qh#L11) → [common/mutators/mutator/status_effects/status_effect/_mod.qh:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/_mod.qh#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:39](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L39) → [common/mutators/mutator/status_effects/_mod.inc:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/_mod.inc#L11) → [common/mutators/mutator/status_effects/status_effect/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/_mod.inc#L2) → [common/mutators/mutator/status_effects/status_effect/burning.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/burning.qc#L1)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L6) → [server/cheats.qc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/cheats.qc#L13) → [common/mutators/mutator/status_effects/_mod.qh:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/_mod.qh#L11) → [common/mutators/mutator/status_effects/status_effect/_mod.qh:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/_mod.qh#L2))

### 90. `common/mutators/mutator/status_effects/status_effect/frozen.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC Frozen is hidden with blue tint and a 10-second default, and first application removes hooks. Native status registration also gives first-application cleanup.

**Open** · Check reapplication, powerup coexistence and rendering on a remote frozen model.

**Sources** · [common/mutators/mutator/status_effects/status_effect/frozen.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/frozen.qh) · [common/mutators/mutator/status_effects/status_effect/frozen.qc:50](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/frozen.qc#L50) · [StatusEffects.cs:160](../../../src/VortexArena.Common/Gameplay/StatusEffects.cs#L160)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L12) → [client/view.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/view.qc#L26) → [common/mutators/mutator/status_effects/_mod.qh:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/_mod.qh#L11) → [common/mutators/mutator/status_effects/status_effect/_mod.qh:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/_mod.qh#L3)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:39](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L39) → [common/mutators/mutator/status_effects/_mod.inc:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/_mod.inc#L11) → [common/mutators/mutator/status_effects/status_effect/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/_mod.inc#L3) → [common/mutators/mutator/status_effects/status_effect/frozen.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/frozen.qc#L1)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L6) → [server/cheats.qc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/cheats.qc#L13) → [common/mutators/mutator/status_effects/_mod.qh:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/_mod.qh#L11) → [common/mutators/mutator/status_effects/status_effect/_mod.qh:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/_mod.qh#L3))

### 91. `common/mutators/mutator/status_effects/status_effect/spawnshield.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC hidden SpawnShield uses a green tint, a 10-second default and sets additive/fullbright after match start. Native sets and clears those bits on the status lifecycle.

**Open** · Test match-start timing, forced removal and coexistence with other sources of the same effect flags.

**Sources** · [common/mutators/mutator/status_effects/status_effect/spawnshield.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/spawnshield.qh) · [common/mutators/mutator/status_effects/status_effect/spawnshield.qc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/spawnshield.qc#L6) · [StatusEffects.cs:184](../../../src/VortexArena.Common/Gameplay/StatusEffects.cs#L184)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L12) → [client/view.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/view.qc#L26) → [common/mutators/mutator/status_effects/_mod.qh:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/_mod.qh#L11) → [common/mutators/mutator/status_effects/status_effect/_mod.qh:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/_mod.qh#L4)) · MENUQC ([menu/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L5) → [menu/_mod.inc:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/_mod.inc#L11) → [menu/xonotic/_mod.inc:132](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/_mod.inc#L132) → [menu/xonotic/guide/_mod.inc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/guide/_mod.inc#L4) → [menu/xonotic/guide/guide.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/guide/guide.qc#L1) → [menu/xonotic/guide/guide.qh:92](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/xonotic/guide/guide.qh#L92) → [common/mutators/mutator/buffs/buffs.qh:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/buffs/buffs.qh#L3) → [common/mutators/mutator/status_effects/_mod.qh:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/_mod.qh#L11) → [common/mutators/mutator/status_effects/status_effect/_mod.qh:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/_mod.qh#L4)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L6) → [server/cheats.qc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/cheats.qc#L13) → [common/mutators/mutator/status_effects/_mod.qh:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/_mod.qh#L11) → [common/mutators/mutator/status_effects/status_effect/_mod.qh:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/_mod.qh#L4))

### 92. `common/mutators/mutator/status_effects/status_effect/stunned.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC hidden Stunned has a 10-second default and spark removal sound and clears shock while frozen or removed. Native status tick maps the same flag and freeze check.

**Open** · Check Arc/Onslaught producer timings and client effect removal across snapshots.

**Sources** · [common/mutators/mutator/status_effects/status_effect/stunned.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/stunned.qh) · [common/mutators/mutator/status_effects/status_effect/stunned.qc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/stunned.qc#L6) · [StatusEffects.cs:195](../../../src/VortexArena.Common/Gameplay/StatusEffects.cs#L195)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L12) → [client/view.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/view.qc#L26) → [common/mutators/mutator/status_effects/_mod.qh:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/_mod.qh#L11) → [common/mutators/mutator/status_effects/status_effect/_mod.qh:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/_mod.qh#L5)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:39](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L39) → [common/mutators/mutator/status_effects/_mod.inc:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/_mod.inc#L11) → [common/mutators/mutator/status_effects/status_effect/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/_mod.inc#L5) → [common/mutators/mutator/status_effects/status_effect/stunned.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/stunned.qc#L1)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L6) → [server/cheats.qc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/cheats.qc#L13) → [common/mutators/mutator/status_effects/_mod.qh:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/_mod.qh#L11) → [common/mutators/mutator/status_effects/status_effect/_mod.qh:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/_mod.qh#L5))

### 93. `common/mutators/mutator/status_effects/status_effect/superweapons.qh`

**Disposition** · counterpart-with-delta · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Paired picker and peer recordings](../review/superweapons-poweroff-verification.md#live-capture)

Both pickers and nearby peers heard six countdown cues on a timed Fireball pickup, one on early loss, and one when unlimited protection ended after the original deadline. The finite HUD row disappears during unlimited overlap. Native used a controlled grant for the early-loss and overlap arms.

**Open** · Native retains the later upstream relative-duration pickup fix adopted in UW-0123. Without prior status initialization the pinned build can consume a late pickup immediately. Separate client clocks limit absolute timing comparisons.

**Sources** · [common/mutators/mutator/status_effects/status_effect/superweapons.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/superweapons.qh) · [common/mutators/mutator/status_effects/status_effect/superweapons.qh:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/superweapons.qh#L10) · [PlayerFrameLogic.cs:572](../../../src/VortexArena.Server/PlayerFrameLogic.cs#L572)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L12) → [client/view.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/view.qc#L26) → [common/mutators/mutator/status_effects/_mod.qh:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/_mod.qh#L11) → [common/mutators/mutator/status_effects/status_effect/_mod.qh:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/_mod.qh#L6)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:39](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L39) → [common/mutators/mutator/status_effects/_mod.inc:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/_mod.inc#L11) → [common/mutators/mutator/status_effects/status_effect/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/_mod.inc#L6) → [common/mutators/mutator/status_effects/status_effect/superweapons.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/superweapons.qc#L1)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L6) → [server/cheats.qc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/cheats.qc#L13) → [common/mutators/mutator/status_effects/_mod.qh:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/_mod.qh#L11) → [common/mutators/mutator/status_effects/status_effect/_mod.qh:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/status_effects/status_effect/_mod.qh#L6))

### 94. `common/mutators/mutator/vampire/vampire.qh`

**Disposition** · architecture-replaced · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC MENUQC supplies a localized Vampire label. Native registry provides runtime lifesteal activation. Guide description and visibility require their own consumers.

**Open** · Verify display registration versus mode rule enablement.

**Sources** · [common/mutators/mutator/vampire/vampire.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/vampire/vampire.qh) · [common/mutators/mutator/vampire/vampire.qh:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/vampire/vampire.qh#L8) · [VampireMutator.cs:15](../../../src/VortexArena.Common/Gameplay/Mutators/VampireMutator.cs#L15)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:42](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L42) → [common/mutators/mutator/vampire/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/vampire/_mod.inc#L2) → [common/mutators/mutator/vampire/vampire.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/vampire/vampire.qc#L1)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:42](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L42) → [common/mutators/mutator/vampire/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/vampire/_mod.inc#L2) → [common/mutators/mutator/vampire/vampire.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/vampire/vampire.qc#L1)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:42](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L42) → [common/mutators/mutator/vampire/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/vampire/_mod.inc#L2) → [common/mutators/mutator/vampire/vampire.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/vampire/vampire.qc#L1))
**Applicable decisions** · [ADR-0003](../../decisions/ADR-0003-source-generators.md)

### 95. `common/mutators/mutator/walljump/walljump.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC registers the localized Wall jumping label. The native Guide resolves the stock description and label independently of runtime activation.

**Open** · Compare name localization and menu predicate separately from walljump physics.

**Sources** · [common/mutators/mutator/walljump/walljump.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/walljump/walljump.qh) · [common/mutators/mutator/walljump/walljump.qh:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/walljump/walljump.qh#L8) · [WalljumpMutator.cs:41](../../../src/VortexArena.Common/Gameplay/Mutators/WalljumpMutator.cs#L41)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:44](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L44) → [common/mutators/mutator/walljump/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/walljump/_mod.inc#L2) → [common/mutators/mutator/walljump/walljump.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/walljump/walljump.qc#L1)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:44](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L44) → [common/mutators/mutator/walljump/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/walljump/_mod.inc#L2) → [common/mutators/mutator/walljump/walljump.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/walljump/walljump.qc#L1)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:52](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L52) → [common/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/_mod.inc#L3) → [common/mutators/mutator/_mod.inc:44](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/_mod.inc#L44) → [common/mutators/mutator/walljump/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/walljump/_mod.inc#L2) → [common/mutators/mutator/walljump/walljump.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/mutators/mutator/walljump/walljump.qc#L1))

### 96. `common/physics/movelib.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC velocity blend supplies 40 percent new desired speed per call in mobile turret and monster paths. Native mobile turret behavior lives in the per-unit controller; no common movelib API carries all drag, simple braking, speed-biased inertia and four-point alignment semantics.

**Open** · Check each active consumer under grounded/airborne transitions and nonstandard frame intervals.

**Sources** · [common/physics/movelib.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/physics/movelib.qh) · [common/turrets/turret/ewheel.qc:55](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/ewheel.qc#L55) · [EWheelTurret.cs:158](../../../src/VortexArena.Common/Gameplay/Turrets/EWheelTurret.cs#L158)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L26) → [common/physics/all.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/physics/all.inc#L2) → [common/physics/movelib.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/physics/movelib.qc#L1)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L26) → [common/physics/all.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/physics/all.inc#L2) → [common/physics/movelib.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/physics/movelib.qc#L1))

### 97. `common/physics/movetypes/movetypes.qh`

**Disposition** · architecture-replaced · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC splits server cvar inputs and client stats for gravity, step, ground and bounce behavior, with explicit movetype IDs. Native dispatches typed moves and uses installed movement parameters and snapshots.

**Open** · Compare forced move IDs under legacy, low tick-rate gravity, ground clearing and effective collision planes separately.

**Sources** · [common/physics/movetypes/movetypes.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/physics/movetypes/movetypes.qh) · [common/physics/movetypes/movetypes.qc:705](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/physics/movetypes/movetypes.qc#L705) · [MoveTypePhysics.cs:31](../../../src/VortexArena.Engine/Simulation/MoveTypePhysics.cs#L31)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L4) → [client/csqcmodel_hooks.qc:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/csqcmodel_hooks.qc#L12)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L9) → [server/damage.qc:16](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/damage.qc#L16))
**Applicable decisions** · [ADR-0010](../../decisions/ADR-0010-determinism-and-numerics.md) · [ADR-0019](../../decisions/ADR-0019-legacy-compatibility-mode.md)

### 98. `common/physics/movetypes/push.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC pusher records temporary position/angle state for entities touched during motion and handles local time. Native runs pusher physics and Think through a typed engine entry.

**Open** · Compare blocked rotational rollback, trigger side effects and passenger movement.

**Sources** · [common/physics/movetypes/push.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/physics/movetypes/push.qh) · [common/physics/movetypes/movetypes.qc:705](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/physics/movetypes/movetypes.qc#L705) · [FlyMove.cs:288](../../../src/VortexArena.Engine/Simulation/FlyMove.cs#L288)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L26) → [common/physics/all.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/physics/all.inc#L3) → [common/physics/movetypes/all.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/physics/movetypes/all.inc#L5) → [common/physics/movetypes/push.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/physics/movetypes/push.qc#L1)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L26) → [common/physics/all.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/physics/all.inc#L3) → [common/physics/movetypes/all.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/physics/movetypes/all.inc#L5) → [common/physics/movetypes/push.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/physics/movetypes/push.qc#L1))

### 99. `common/turrets/all.qc`

**Disposition** · architecture-replaced · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC registers a dedicated linked turret entity for client setup and updates. Native turret bases and heads participate in ordinary entity snapshots.

**Open** · Verify linked visibility, late-join model/head state and effects and animation across PVS changes.

**Sources** · [common/turrets/all.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/all.qc) · [common/turrets/cl_turrets.qc:354](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/cl_turrets.qc#L354) · [ServerNet.cs:3444](../../../game/net/ServerNet.cs#L3444)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L2)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L2))
**Applicable decisions** · [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md)

### 100. `common/turrets/config.qc`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

The dedicated stock and native default Darkzone exports were byte-identical for 12 turrets and 370 settings. The local native command supports default and custom paths and console echo.

**Open** · Compare unusual mod-declared properties and customized balances outside the verified stock catalog.

**Sources** · [common/turrets/config.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/config.qc) · [common/turrets/all.qh:91](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/all.qh#L91) · [Commands.LocalDiagnostics.cs:34](../../../src/VortexArena.Server/Commands.LocalDiagnostics.cs#L34)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L4)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L4)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L4))
**Applicable decisions** · [ADR-0018](../../decisions/ADR-0018-config-layer.md)

### 101. `common/turrets/config.qh`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

The dedicated stock and native default Darkzone exports were byte-identical for 12 turrets and 370 settings. The local native command supports default and custom paths and console echo.

**Open** · Compare unusual mod-declared properties and customized balances outside the verified stock catalog.

**Sources** · [common/turrets/config.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/config.qh) · [common/turrets/config.qc:55](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/config.qc#L55) · [Commands.LocalDiagnostics.cs:34](../../../src/VortexArena.Server/Commands.LocalDiagnostics.cs#L34)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L4) → [common/turrets/config.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/config.qc#L1)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L4) → [common/turrets/config.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/config.qc#L1)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:37](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L37) → [common/command/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/command/_mod.inc#L2) → [common/command/generic.qc:19](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/command/generic.qc#L19))
**Applicable decisions** · [ADR-0018](../../decisions/ADR-0018-config-layer.md)

### 102. `common/turrets/sv_turrets.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

The QC server contract supplies scan/reload settings, aim modes, tracking fields, ammo and per-map scales. Native uses a typed common turret state plus per-unit run/combat callbacks.

**Open** · Compare target validation throttle, cvar reload, head angles and per-map scaling with turret fixtures.

**Sources** · [common/turrets/sv_turrets.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/sv_turrets.qh) · [common/turrets/sv_turrets.qc:874](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/sv_turrets.qc#L874) · [TurretAI.cs:643](../../../src/VortexArena.Common/Gameplay/Turrets/TurretAI.cs#L643)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L7) → [server/client.qc:38](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/client.qc#L38) → [common/vehicles/sv_vehicles.qh:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/sv_vehicles.qh#L4))

### 103. `common/turrets/turret/ewheel_weapon.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC registers turret_ewheel as a hidden special attack with impulse 5, using a PortoLaunch subclass. Native routes the map turret's combat through its typed controller. The native firing counterpart alone does not establish parity for player invocation of the QC special attack.

**Open** · Model-less Blaster-class bolt from the turret muzzle with the eWheel movement state; compare projectile health, impact radius and metadata of the QC player arm.

**Sources** · [common/turrets/turret/ewheel_weapon.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/ewheel_weapon.qh) · [common/turrets/turret/ewheel_weapon.qc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/ewheel_weapon.qc#L7) · [EWheelTurret.cs:29](../../../src/VortexArena.Common/Gameplay/Turrets/EWheelTurret.cs#L29)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L3) → [common/turrets/turret/ewheel_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/ewheel_weapon.qc#L1)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L3) → [common/turrets/turret/ewheel_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/ewheel_weapon.qc#L1)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L3) → [common/turrets/turret/ewheel_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/ewheel_weapon.qc#L1))
**Applicable decisions** · [ADR-0003](../../decisions/ADR-0003-source-generators.md)

### 104. `common/turrets/turret/hellion_weapon.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC registers turret_hellion as a hidden special attack with impulse 9, using a PortoLaunch subclass. Native routes the map turret's combat through its typed controller. The native firing counterpart alone does not establish parity for player invocation of the QC special attack.

**Open** · Guided rocket dispatched with the selected target; compare seeker lock loss, muzzle offset and guidance after target death.

**Sources** · [common/turrets/turret/hellion_weapon.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/hellion_weapon.qh) · [common/turrets/turret/hellion_weapon.qc:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/hellion_weapon.qc#L10) · [HellionTurret.cs:21](../../../src/VortexArena.Common/Gameplay/Turrets/HellionTurret.cs#L21)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L8) → [common/turrets/turret/hellion_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/hellion_weapon.qc#L1)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L8) → [common/turrets/turret/hellion_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/hellion_weapon.qc#L1)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L8) → [common/turrets/turret/hellion_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/hellion_weapon.qc#L1))
**Applicable decisions** · [ADR-0003](../../decisions/ADR-0003-source-generators.md)

### 105. `common/turrets/turret/machinegun_weapon.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC registers turret_machinegun as a hidden special attack with impulse 9, using a PortoLaunch subclass. Native routes the map turret's combat through its typed controller. The native firing counterpart alone does not establish parity for player invocation of the QC special attack.

**Open** · Hitscan with spread, source, force and death tag; QC has a player refire branch through sustained MachineGun cvar.

**Sources** · [common/turrets/turret/machinegun_weapon.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/machinegun_weapon.qh) · [common/turrets/turret/machinegun_weapon.qc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/machinegun_weapon.qc#L7) · [MachinegunTurret.cs:19](../../../src/VortexArena.Common/Gameplay/Turrets/MachinegunTurret.cs#L19)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L12) → [common/turrets/turret/machinegun_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/machinegun_weapon.qc#L1)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L12) → [common/turrets/turret/machinegun_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/machinegun_weapon.qc#L1)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:12](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L12) → [common/turrets/turret/machinegun_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/machinegun_weapon.qc#L1))
**Applicable decisions** · [ADR-0003](../../decisions/ADR-0003-source-generators.md)

### 106. `common/turrets/turret/mlrs_weapon.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC registers turret_mlrs as a hidden special attack with impulse 9, using a PortoLaunch subclass. Native routes the map turret's combat through its typed controller. The native firing counterpart alone does not establish parity for player invocation of the QC special attack.

**Open** · Unguided rocket in a rapid volley; compare low-ammo burst continuation and the separate QC player refire arm.

**Sources** · [common/turrets/turret/mlrs_weapon.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/mlrs_weapon.qh) · [common/turrets/turret/mlrs_weapon.qc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/mlrs_weapon.qc#L6) · [MlrsTurret.cs:21](../../../src/VortexArena.Common/Gameplay/Turrets/MlrsTurret.cs#L21)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L14) → [common/turrets/turret/mlrs_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/mlrs_weapon.qc#L1)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L14) → [common/turrets/turret/mlrs_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/mlrs_weapon.qc#L1)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L14) → [common/turrets/turret/mlrs_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/mlrs_weapon.qc#L1))
**Applicable decisions** · [ADR-0003](../../decisions/ADR-0003-source-generators.md)

### 107. `common/turrets/turret/phaser_weapon.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC registers turret_phaser as a hidden special attack with impulse 9, using a PortoLaunch subclass. Native routes the map turret's combat through its typed controller. The native firing counterpart alone does not establish parity for player invocation of the QC special attack.

**Open** · Persistent penetrating beam and slowdown tick; compare initial trace, beam termination and discharge.

**Sources** · [common/turrets/turret/phaser_weapon.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/phaser_weapon.qh) · [common/turrets/turret/phaser_weapon.qc:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/phaser_weapon.qc#L10) · [PhaserTurret.cs:23](../../../src/VortexArena.Common/Gameplay/Turrets/PhaserTurret.cs#L23)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:16](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L16) → [common/turrets/turret/phaser_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/phaser_weapon.qc#L1)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:16](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L16) → [common/turrets/turret/phaser_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/phaser_weapon.qc#L1)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:16](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L16) → [common/turrets/turret/phaser_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/phaser_weapon.qc#L1))
**Applicable decisions** · [ADR-0003](../../decisions/ADR-0003-source-generators.md)

### 108. `common/turrets/turret/plasma_weapon.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC registers turret_plasma as a hidden special attack with impulse 5, using a PortoLaunch subclass. Native routes the map turret's combat through its typed controller. The native firing counterpart alone does not establish parity for player invocation of the QC special attack.

**Open** · Plasma projectile and an Instagib rail branch in the turret fire path; compare launch effects, damage and obstruction.

**Sources** · [common/turrets/turret/plasma_weapon.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/plasma_weapon.qh) · [common/turrets/turret/plasma_weapon.qc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/plasma_weapon.qc#L6) · [PlasmaTurret.cs:20](../../../src/VortexArena.Common/Gameplay/Turrets/PlasmaTurret.cs#L20)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:19](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L19) → [common/turrets/turret/plasma_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/plasma_weapon.qc#L1)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:19](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L19) → [common/turrets/turret/plasma_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/plasma_weapon.qc#L1)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:19](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L19) → [common/turrets/turret/plasma_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/plasma_weapon.qc#L1))
**Applicable decisions** · [ADR-0003](../../decisions/ADR-0003-source-generators.md)

### 109. `common/turrets/turret/tesla_weapon.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC registers turret_tesla as a hidden special attack with impulse 9, using a PortoLaunch subclass. Native routes the map turret's combat through its typed controller. The native firing counterpart alone does not establish parity for player invocation of the QC special attack.

**Open** · Tesla area shock with trace and chain decisions; compare recipient ordering and cooldown when no target is hit.

**Sources** · [common/turrets/turret/tesla_weapon.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/tesla_weapon.qh) · [common/turrets/turret/tesla_weapon.qc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/tesla_weapon.qc#L7) · [TeslaTurret.cs:25](../../../src/VortexArena.Common/Gameplay/Turrets/TeslaTurret.cs#L25)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:21](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L21) → [common/turrets/turret/tesla_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/tesla_weapon.qc#L1)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:21](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L21) → [common/turrets/turret/tesla_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/tesla_weapon.qc#L1)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:21](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L21) → [common/turrets/turret/tesla_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/tesla_weapon.qc#L1))
**Applicable decisions** · [ADR-0003](../../decisions/ADR-0003-source-generators.md)

### 110. `common/turrets/turret/walker_weapon.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC registers turret_walker as a hidden special attack with impulse 5, using a PortoLaunch subclass. Native routes the map turret's combat through its typed controller. The native firing counterpart alone does not establish parity for player invocation of the QC special attack.

**Open** · Primary hitscan from the Walker plus a separate chassis rocket attack; compare shot tagging and interactions with the locomotion controller.

**Sources** · [common/turrets/turret/walker_weapon.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/walker_weapon.qh) · [common/turrets/turret/walker_weapon.qc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/walker_weapon.qc#L6) · [WalkerTurret.cs:30](../../../src/VortexArena.Common/Gameplay/Turrets/WalkerTurret.cs#L30)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:23](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L23) → [common/turrets/turret/walker_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/walker_weapon.qc#L1)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:23](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L23) → [common/turrets/turret/walker_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/walker_weapon.qc#L1)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:23](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L23) → [common/turrets/turret/walker_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/walker_weapon.qc#L1))
**Applicable decisions** · [ADR-0003](../../decisions/ADR-0003-source-generators.md)

### 111. `common/vehicles/all.qc`

**Disposition** · architecture-replaced · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC registers linked auxiliary markers. Native reads vehicle aim state and installs projected marker slots on VehicleHud each updated frame.

**Open** · Compare expiry after losing PVS, seat exit and stale slot cleanup.

**Sources** · [common/vehicles/all.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/all.qc) · [common/vehicles/cl_vehicles.qc:32](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/cl_vehicles.qc#L32) · [NetGame.cs:6268](../../../game/net/NetGame.cs#L6268)
**Include routes** · CSQC ([client/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L7) → [common/_all.inc:50](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L50) → [common/vehicles/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/_mod.inc#L2)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:50](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L50) → [common/vehicles/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/_mod.inc#L2)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:50](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L50) → [common/vehicles/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/_mod.inc#L2))
**Applicable decisions** · [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md)

### 112. `common/vehicles/all.qh`

**Disposition** · architecture-replaced · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC registry reserves ID 0 for hidden fallback and selects side-specific headers. Native typed discovery assigns registries and resolves vehicle definitions by name.

**Open** · Compare unknown vehicle entry and legacy numeric ID roundtrip independently.

**Sources** · [common/vehicles/all.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/all.qh) · [common/vehicles/all.qh:23](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/all.qh#L23) · [VehicleCommon.cs:148](../../../src/VortexArena.Common/Gameplay/Vehicles/VehicleCommon.cs#L148)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:32](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L32)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:50](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L50) → [common/vehicles/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/_mod.inc#L2) → [common/vehicles/all.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/all.qc#L1)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L3) → [server/antilag.qc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/antilag.qc#L6))
**Applicable decisions** · [ADR-0003](../../decisions/ADR-0003-source-generators.md) · [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md)

### 113. `common/vehicles/cl_vehicles.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Native reticle fixture and its input limits](../vehicle-reticles/README.md)

Four auxiliary slots accept vehicle updates. Native uses the 0.5 default with the actual texture dimensions for the main, auxiliary and drop reticles. The shipped config also sets 0.5. A zero size suppresses the marker, and an auxiliary update expires after 0.1 seconds.

**Open** · Compare lock-on blending and a remote pilot switching vehicle modes.

**Sources** · [common/vehicles/cl_vehicles.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/cl_vehicles.qh) · [common/vehicles/cl_vehicles.qc:67](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/cl_vehicles.qc#L67) · [VehicleHud.cs:212](../../../game/hud/VehicleHud.cs#L212)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:32](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L32) → [common/vehicles/all.qh:17](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/all.qh#L17))

### 114. `common/vehicles/vehicle/racer_weapon.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC dispatches hidden weapon impulse 3 from vehicle logic, with independent energy cannon and guided rocket cvars. Native vehicle dispatch calls methods with player/vehicle/muzzle context.

**Open** · Check effective stock overrides, rocket lock angle, shared timer and projectile lead.

**Sources** · [common/vehicles/vehicle/racer_weapon.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/vehicle/racer_weapon.qh) · [common/vehicles/vehicle/racer.qc:311](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/vehicle/racer.qc#L311) · [Racer.cs:716](../../../src/VortexArena.Common/Gameplay/Vehicles/Racer.cs#L716)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:32](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L32) → [common/vehicles/all.qh:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/all.qh#L26) → [common/vehicles/vehicle/_mod.qh:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/vehicle/_mod.qh#L5)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:50](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L50) → [common/vehicles/_mod.inc:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/_mod.inc#L11) → [common/vehicles/vehicle/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/vehicle/_mod.inc#L5) → [common/vehicles/vehicle/racer_weapon.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/vehicle/racer_weapon.qc#L1)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L3) → [server/antilag.qc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/antilag.qc#L6) → [common/vehicles/all.qh:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/all.qh#L26) → [common/vehicles/vehicle/_mod.qh:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/vehicle/_mod.qh#L5))

### 115. `common/vehicles/vehicle/raptor_weapons.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC hidden impulse-3 attacks cover cannon, cluster bombs and decoy flares; client declares separate shell-fragment drawing. Native vehicle drives cannon and secondary mode methods.

**Open** · Check bomblet split delay/spread, flare chase against competing grounded projectiles and renderer fragments.

**Sources** · [common/vehicles/vehicle/raptor_weapons.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/vehicle/raptor_weapons.qh) · [common/vehicles/vehicle/raptor.qc:375](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/vehicle/raptor.qc#L375) · [Raptor.cs:692](../../../src/VortexArena.Common/Gameplay/Vehicles/Raptor.cs#L692)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:32](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L32) → [common/vehicles/all.qh:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/all.qh#L26) → [common/vehicles/vehicle/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/vehicle/_mod.qh#L7)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:50](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L50) → [common/vehicles/_mod.inc:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/_mod.inc#L11) → [common/vehicles/vehicle/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/vehicle/_mod.inc#L7) → [common/vehicles/vehicle/raptor_weapons.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/vehicle/raptor_weapons.qc#L1)) · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L3) → [server/antilag.qc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/antilag.qc#L6) → [common/vehicles/all.qh:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/all.qh#L26) → [common/vehicles/vehicle/_mod.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/vehicles/vehicle/_mod.qh#L7))

### 116. `common/weapons/calculations.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC shared spread includes a four-standard-deviation cap, pattern bias and normalized/non-normalized modes, and also exposes gun alignment and explosion push. Native splits those into firing, slot and damage routines.

**Open** · Pair random distributions, pattern order, speed-factor force and deprecated gun-alignment preferences.

**Sources** · [common/weapons/calculations.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/calculations.qh) · [common/weapons/calculations.qc:226](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/calculations.qc#L226) · [WeaponFiring.cs:1012](../../../src/VortexArena.Common/Gameplay/Weapons/WeaponFiring.cs#L1012)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L2) → [client/announcer.qc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/announcer.qc#L7) → [common/notifications/all.qh:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/notifications/all.qh#L10) → [common/weapons/all.qh:20](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.qh#L20)) · SVQC ([server/progs.inc:21](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L21) → [lib/warpzone/common.qc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/warpzone/common.qc#L8) → [common/weapons/_all.qh:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/_all.qh#L2) → [common/weapons/all.qh:20](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.qh#L20))

### 117. `common/weapons/config.qc`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

The dedicated stock and native default Darkzone exports were byte-identical for 25 weapons and 851 settings. The local native command supports default and custom paths and console echo.

**Open** · Compare unusual mod-declared properties and customized balances outside the verified stock catalog.

**Sources** · [common/weapons/config.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/config.qc) · [common/weapons/all.qh:80](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.qh#L80) · [Commands.LocalDiagnostics.cs:32](../../../src/VortexArena.Server/Commands.LocalDiagnostics.cs#L32)
**Include routes** · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:47](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L47) → [common/weapons/_all.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/_all.inc#L2) → [common/weapons/all.qc:32](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.qc#L32))
**Applicable decisions** · [ADR-0018](../../decisions/ADR-0018-config-layer.md)

### 118. `common/weapons/config.qh`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

The dedicated stock and native default Darkzone exports were byte-identical for 25 weapons and 851 settings. The local native command supports default and custom paths and console echo.

**Open** · Compare unusual mod-declared properties and customized balances outside the verified stock catalog.

**Sources** · [common/weapons/config.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/config.qh) · [common/weapons/config.qc:74](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/config.qc#L74) · [Commands.LocalDiagnostics.cs:32](../../../src/VortexArena.Server/Commands.LocalDiagnostics.cs#L32)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L2) → [client/announcer.qc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/announcer.qc#L7) → [common/notifications/all.qh:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/notifications/all.qh#L10) → [common/weapons/all.qh:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.qh#L5)) · MENUQC ([menu/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L5) → [menu/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/_mod.inc#L5) → [menu/menu.qc:22](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/menu.qc#L22) → [common/weapons/_all.qh:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/_all.qh#L2) → [common/weapons/all.qh:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.qh#L5)) · SVQC ([server/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L15) → [common/_all.inc:37](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L37) → [common/command/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/command/_mod.inc#L2) → [common/command/generic.qc:20](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/command/generic.qc#L20))
**Applicable decisions** · [ADR-0018](../../decisions/ADR-0018-config-layer.md)

### 119. `common/weapons/weapon/electro.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC registers cell ammo, impulse 5, bolt and orb models, and the combo, wet damage and stick settings. Native tracks live orbs by owner and launch age even if sticking changes their remaining lifetime.

**Open** · Check balance overrides and the two live attacks in a paired session.

**Sources** · [common/weapons/weapon/electro.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/weapon/electro.qh) · [common/weapons/weapon/electro.qc:574](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/weapon/electro.qc#L574) · [Electro.cs:646](../../../src/VortexArena.Common/Gameplay/Weapons/Electro.cs#L646)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L2) → [client/announcer.qc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/announcer.qc#L7) → [common/notifications/all.qh:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/notifications/all.qh#L10) → [common/weapons/all.qh:347](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.qh#L347) → [common/weapons/all.inc:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.inc#L11)) · MENUQC ([menu/progs.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L7) → [common/_all.inc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/_all.inc#L49) → [common/turrets/_mod.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/_mod.inc#L14) → [common/turrets/turret/_mod.inc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/_mod.inc#L4) → [common/turrets/turret/flac.qc:22](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/flac.qc#L22)) · SVQC ([server/progs.inc:21](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L21) → [lib/warpzone/common.qc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/warpzone/common.qc#L8) → [common/weapons/_all.qh:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/_all.qh#L2) → [common/weapons/all.qh:347](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.qh#L347) → [common/weapons/all.inc:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.inc#L11))

### 120. `common/weapons/weapon/shotgun.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC uses shells and impulse 2, separates pellet falloff/pattern and melee trace/slap controls, and registers flash, sounds and alternate crosshair. Native has a dedicated Shotgun wrapper around bullet and melee actions.

**Open** · Verify alternate modes, multi-hit ordering, pellet distribution and crosshair/flash data consumers.

**Sources** · [common/weapons/weapon/shotgun.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/weapon/shotgun.qh) · [common/weapons/weapon/shotgun.qc:275](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/weapon/shotgun.qc#L275) · [Shotgun.cs:26](../../../src/VortexArena.Common/Gameplay/Weapons/Shotgun.cs#L26)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L2) → [client/announcer.qc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/announcer.qc#L7) → [common/notifications/all.qh:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/notifications/all.qh#L10) → [common/weapons/all.qh:347](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.qh#L347) → [common/weapons/all.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.inc#L7)) · MENUQC ([menu/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L5) → [menu/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/_mod.inc#L5) → [menu/menu.qc:22](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/menu.qc#L22) → [common/weapons/_all.qh:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/_all.qh#L2) → [common/weapons/all.qh:347](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.qh#L347) → [common/weapons/all.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.inc#L7)) · SVQC ([server/progs.inc:21](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L21) → [lib/warpzone/common.qc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/warpzone/common.qc#L8) → [common/weapons/_all.qh:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/_all.qh#L2) → [common/weapons/all.qh:347](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.qh#L347) → [common/weapons/all.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.inc#L7))

### 121. `common/weapons/weapon/vaporizer.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC registers vaporizer/minstanex aliases, superweapon and no-dual flags, rail falloff and reticle, and player state for jump/laser intervals. Native has a dedicated Vaporizer class and native crosshair reticle mapping.

**Open** · Check deprecated netname resolution, starting balance, switch delays and paired jump/shot intervals.

**Sources** · [common/weapons/weapon/vaporizer.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/weapon/vaporizer.qh) · [common/weapons/weapon/vaporizer.qh:66](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/weapon/vaporizer.qh#L66) · [Vaporizer.cs:21](../../../src/VortexArena.Common/Gameplay/Weapons/Vaporizer.cs#L21)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L2) → [client/announcer.qc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/announcer.qc#L7) → [common/notifications/all.qh:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/notifications/all.qh#L10) → [common/weapons/all.qh:347](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.qh#L347) → [common/weapons/all.inc:19](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.inc#L19)) · MENUQC ([menu/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L5) → [menu/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/_mod.inc#L5) → [menu/menu.qc:22](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/menu.qc#L22) → [common/weapons/_all.qh:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/_all.qh#L2) → [common/weapons/all.qh:347](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.qh#L347) → [common/weapons/all.inc:19](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.inc#L19)) · SVQC ([server/progs.inc:21](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L21) → [lib/warpzone/common.qc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/warpzone/common.qc#L8) → [common/weapons/_all.qh:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/_all.qh#L2) → [common/weapons/all.qh:347](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.qh#L347) → [common/weapons/all.inc:19](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/weapons/all.inc#L19))

### 122. `lib/angle.qc`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC wraps model angles into [-180,180), offers short-angle comparison against a second input and an offset-from-target helper. Native TurretAI/EWheel uses a dedicated AngleMods routine.

**Open** · Compare exact seams, half turns and other consumers such as vehicles with inputs outside one rotation.

**Sources** · [lib/angle.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/angle.qc) · [common/turrets/sv_turrets.qc:554](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/sv_turrets.qc#L554) · [TurretMath.cs:25](../../../src/VortexArena.Common/Gameplay/Turrets/TurretMath.cs#L25)
**Include routes** · CSQC ([client/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L1) → [lib/_all.inc:112](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L112)) · MENUQC ([menu/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L1) → [lib/_all.inc:112](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L112)) · SVQC ([server/progs.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L9) → [lib/_all.inc:112](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L112))

### 123. `lib/bits.qh`

**Disposition** · architecture-replaced · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC mutates 24-bit-exact masks through GiveBit/GiveValue and samples set bits without replacement. Native item/grant paths use typed flags and token operations.

**Open** · Check compound flag deltas, min/max semantics, random selection and error return when population is smaller than k.

**Sources** · [lib/bits.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/bits.qh) · [server/items/items.qc:1505](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/items/items.qc#L1505) · [TargetUtilities.cs:668](../../../src/VortexArena.Common/Gameplay/MapObjects/TargetUtilities.cs#L668)
**Include routes** · CSQC ([client/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L1) → [lib/_all.inc:114](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L114)) · MENUQC ([menu/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L1) → [lib/_all.inc:114](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L114)) · SVQC ([server/progs.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L9) → [lib/_all.inc:114](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L114))
**Applicable decisions** · [ADR-0007](../../decisions/ADR-0007-entity-model.md) · [ADR-0010](../../decisions/ADR-0010-determinism-and-numerics.md)

### 124. `lib/color.qh`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC uses 15 fixed colors plus distinct animated shirt/pants index 15 and implements HSV/HSL conversion and legacy hex markup. Native ModelTint and NativeColour carry palette and conversion logic for appearance.

**Open** · Compare each caller's colorspace, hue wrap, invalid index defaults and animated phase.

**Sources** · [lib/color.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/color.qh) · [lib/color.qh:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/color.qh#L7) · [ModelTint.cs:25](../../../game/client/ModelTint.cs#L25)
**Include routes** · CSQC ([client/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L1) → [lib/_all.inc:115](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L115)) · MENUQC ([menu/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L1) → [lib/_all.inc:115](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L115)) · SVQC ([server/progs.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L9) → [lib/_all.inc:115](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L115))

### 125. `lib/defer.qh`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC makes an owned pure edict, schedules Think, switches Think to removal before invoking the callback, and deletes on its next execution. Native DeferredCommands has scheduled command actions, with no universal owner-edict wrapper.

**Open** · Trace same-tick and zero-delay ordering, owner deletion and close/reset behavior per consumer.

**Sources** · [lib/defer.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/defer.qh) · [common/net_notice.qc:18](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/net_notice.qc#L18) · [DeferredCommands.cs:21](../../../src/VortexArena.Server/DeferredCommands.cs#L21)
**Include routes** · CSQC ([client/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L1) → [lib/_all.inc:118](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L118)) · MENUQC ([menu/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L1) → [lib/_all.inc:118](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L118)) · SVQC ([server/progs.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L9) → [lib/_all.inc:118](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L118))

### 126. `lib/file.qh`

**Disposition** · architecture-replaced · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC tests readable virtual paths via fopen and closes the descriptor, as used for model and map checks. Native asset loading resolves through VFS.Exists and archive mounts.

**Open** · Compare writable overlay/mount precedence, inaccessible files and path casing.

**Sources** · [lib/file.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/file.qh) · [common/models/model.qh:16](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/models/model.qh#L16) · [VirtualFileSystem.cs:457](../../../src/VortexArena.Formats/Vfs/VirtualFileSystem.cs#L457)
**Include routes** · CSQC ([client/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L1) → [lib/_all.inc:121](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L121)) · MENUQC ([menu/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L1) → [lib/_all.inc:121](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L121)) · SVQC ([server/progs.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L9) → [lib/_all.inc:121](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L121))
**Applicable decisions** · [ADR-0006](../../decisions/ADR-0006-asset-pipeline.md)

### 127. `lib/float.qh`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC epsilon is 2^-23, used by scaled comparison and CTF slope clamping. Its max is finite IEEE float and NaN follows the VM caveat. C# float.Epsilon is subnormal, so numeric substitution must be checked at consumers.

**Open** · Check near-zero comparisons and NaN boundaries in score/physics code.

**Sources** · [lib/float.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/float.qh) · [lib/math.qh:233](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/math.qh#L233) · [QMath.cs:17](../../../src/VortexArena.Common/Math/QMath.cs#L17)
**Include routes** · CSQC ([client/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L1) → [lib/_all.inc:122](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L122)) · MENUQC ([menu/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L1) → [lib/_all.inc:122](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L122)) · SVQC ([server/progs.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L9) → [lib/_all.inc:122](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L122))
**Applicable decisions** · [ADR-0010](../../decisions/ADR-0010-determinism-and-numerics.md)

### 128. `lib/i18n.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC probes language-suffixed campaign files and caches stripping of a caret context prefix longer than one char and without a space. Native localization loads translation data, so translated text and localized file selection are separate.

**Open** · Compare campaign fallback and ambiguous leading color text through menu controls.

**Sources** · [lib/i18n.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/i18n.qh) · [common/campaign_file.qc:30](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/campaign_file.qc#L30) · [Localization.cs:24](../../../game/menu/framework/Localization.cs#L24)
**Include routes** · CSQC ([client/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L1) → [lib/_all.inc:124](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L124)) · MENUQC ([menu/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L1) → [lib/_all.inc:124](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L124)) · SVQC ([server/progs.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L9) → [lib/_all.inc:124](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L124))

### 129. `lib/markdown.qh`

**Disposition** · no-stock-caller-evidenced · inventory `unresolved`.
**Evidence quality** · Negative caller search and static source reading.

QC helper preserves paragraphs, treats double-space newline as a hard break and has a source test. Search found the test as its call site. Native guide values use their own layout and cannot establish equivalent wrapping.

**Open** · Check conditional macro-generated or out-of-tree use before declaring unused.

**Sources** · [lib/markdown.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/markdown.qh) · [lib/markdown.qh:46](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/markdown.qh#L46) · [GuidePages.cs:12](../../../game/menu/dialogs/GuidePages.cs#L12)
**Include routes** · CSQC ([client/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L1) → [lib/_all.inc:132](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L132)) · MENUQC ([menu/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L1) → [lib/_all.inc:132](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L132)) · SVQC ([server/progs.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L9) → [lib/_all.inc:132](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L132))

### 130. `lib/misc.qh`

**Disposition** · architecture-replaced · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC supports two preprocessing implementations of argument-count overloads, target-specific edict conversion, and temporary save/restore via WITH. C# overloads and typed entities replace these mechanisms.

**Open** · Audit any caller where temporary mutation scope or edict reuse is observable.

**Sources** · [lib/misc.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/misc.qh) · [common/debug.qh:486](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/debug.qh#L486) · [Entity.cs:31](../../../src/VortexArena.Common/Framework/Entity.cs#L31)
**Include routes** · CSQC ([client/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L1) → [lib/_all.inc:134](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L134)) · MENUQC ([menu/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L1) → [lib/_all.inc:134](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L134)) · SVQC ([server/progs.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L9) → [lib/_all.inc:134](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L134))
**Applicable decisions** · [ADR-0007](../../decisions/ADR-0007-entity-model.md)

### 131. `lib/noise.qh`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

The two active night-vision streams are sampled separately in native play. The fixed-camera Stormkeep fixture exercised ordinary lighting, restricted fullbright and fakelight, animation, and server permission changes without reconnecting.

**Open** · Compare noise distributions across frame rates. Other noise helpers require their own active consumers.

**Sources** · [lib/noise.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/noise.qh) · [client/view.qc:1136](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/view.qc#L1136) · [NightVisionOverlay.cs:19](../../../game/client/NightVisionOverlay.cs#L19)
**Include routes** · CSQC ([client/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L1) → [lib/_all.inc:137](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L137)) · MENUQC ([menu/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L1) → [lib/_all.inc:137](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L137)) · SVQC ([server/progs.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L9) → [lib/_all.inc:137](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L137))

### 132. `lib/p2mathlib.qc`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC implements componentwise position clamps and angle helpers used by its camera. Native now clamps against the sorted camera endpoints and applies track, hard- and close-snap angles. The ordered gameplay fixture exercises both bounds and snapping.

**Open** · The paired FREEAIM fixture exercised combined snap and reentry. General angle wrapping and oblique rails remain open.

**Sources** · [lib/p2mathlib.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/p2mathlib.qc) · [common/viewloc.qc:112](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/viewloc.qc#L112) · [ViewLocationState.cs:70](../../../src/VortexArena.Common/Gameplay/MapObjects/ViewLocationState.cs#L70)
**Include routes** · CSQC ([client/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L1) → [lib/_all.inc:139](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L139)) · MENUQC ([menu/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L1) → [lib/_all.inc:139](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L139)) · SVQC ([server/progs.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L9) → [lib/_all.inc:139](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L139))

### 133. `lib/p2mathlib.qh`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC implements componentwise position clamps and angle helpers used by its camera. Native now clamps against the sorted camera endpoints and applies track, hard- and close-snap angles. The ordered gameplay fixture exercises both bounds and snapping.

**Open** · The paired FREEAIM fixture exercised combined snap and reentry. General angle wrapping and oblique rails remain open.

**Sources** · [lib/p2mathlib.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/p2mathlib.qh) · [common/viewloc.qc:126](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/viewloc.qc#L126) · [ViewLocationState.cs:70](../../../src/VortexArena.Common/Gameplay/MapObjects/ViewLocationState.cs#L70)
**Include routes** · No stock route recorded.

### 134. `lib/promise.qc`

**Disposition** · no-stock-caller-evidenced · inventory `unresolved`.
**Evidence quality** · Negative caller search and static source reading.

QC runs resolve/reject handlers synchronously, chains returned promises and releases intrusive handlers and references. Direct searches outside promise sources did not locate a stock constructor or subscriber.

**Open** · Look for preprocessor-generated consumers and then match state/reentrancy if found.

**Sources** · [lib/promise.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/promise.qc) · [lib/promise.qc:60](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/promise.qc#L60) · [DeferredCommands.cs:21](../../../src/VortexArena.Server/DeferredCommands.cs#L21)
**Include routes** · CSQC ([client/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L1) → [lib/_all.inc:141](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L141)) · MENUQC ([menu/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L1) → [lib/_all.inc:141](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L141)) · SVQC ([server/progs.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L9) → [lib/_all.inc:141](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L141))

### 135. `lib/promise.qh`

**Disposition** · no-stock-caller-evidenced · inventory `unresolved`.
**Evidence quality** · Negative caller search and static source reading.

The public macros construct a downstream edict and forward caller data, with sleep scheduling resolution outside MENUQC. No direct stock call to those macros or sleep was found.

**Open** · Verify preprocessing and separately hosted modules before treating the contract as unused.

**Sources** · [lib/promise.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/promise.qh) · [lib/promise.qc:202](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/promise.qc#L202) · [DeferredCommands.cs:21](../../../src/VortexArena.Server/DeferredCommands.cs#L21)
**Include routes** · CSQC ([client/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L1) → [lib/_all.inc:141](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L141) → [lib/promise.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/promise.qc#L1)) · MENUQC ([menu/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L1) → [lib/_all.inc:141](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L141) → [lib/promise.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/promise.qc#L1)) · SVQC ([server/progs.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L9) → [lib/_all.inc:141](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L141) → [lib/promise.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/promise.qc#L1))

### 136. `lib/string.qh`

**Disposition** · architecture-replaced · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC bundles temp-string ownership, color parsing, clock formatting, token helpers, buffers and page paragraph macros. Native uses managed strings and separate chat, HUD, locale and config functions.

**Open** · Compare UTF-8 length caps, escaped color tags, time rounding and guide formatting at their consumers.

**Sources** · [lib/string.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/string.qh) · [server/chat.qc:57](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/chat.qc#L57) · [HudText.cs:22](../../../game/hud/HudText.cs#L22)
**Include routes** · CSQC ([client/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L1) → [lib/_all.inc:151](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L151)) · MENUQC ([menu/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L1) → [lib/_all.inc:151](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L151)) · SVQC ([server/progs.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L9) → [lib/_all.inc:151](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L151))
**Applicable decisions** · [ADR-0001](../../decisions/ADR-0001-rewrite-strategy.md)

### 137. `lib/vector.qh`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC vector utilities supply AABB predicates, including teleport overlap from the bot header. Native uses typed vectors and consumer-specific AABB checks.

**Open** · Compare touching faces, inverted bounds, random endpoints and distance at threshold.

**Sources** · [lib/vector.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/vector.qh) · [server/bot/default/navigation.qh:64](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/bot/default/navigation.qh#L64) · [BotDanger.cs:91](../../../src/VortexArena.Server/Bot/BotDanger.cs#L91)
**Include routes** · CSQC ([client/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L1) → [lib/_all.inc:156](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L156)) · MENUQC ([menu/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L1) → [lib/_all.inc:156](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L156)) · SVQC ([server/progs.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L9) → [lib/_all.inc:156](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L156))
**Applicable decisions** · [ADR-0010](../../decisions/ADR-0010-determinism-and-numerics.md)

### 138. `lib/csqcmodel/cl_model.qh`

**Disposition** · architecture-replaced · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC macro expands fields and retains animation frames before delta decode. Native pose interpolation stores typed snapshots, while scene animation keeps its own track state.

**Open** · Compare four-frame blending, attachments and teleport reset.

**Sources** · [lib/csqcmodel/cl_model.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/csqcmodel/cl_model.qh) · [lib/csqcmodel/cl_model.qc:250](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/csqcmodel/cl_model.qc#L250) · [SnapshotInterpolation.cs:103](../../../src/VortexArena.Net/SnapshotInterpolation.cs#L103)
**Include routes** · CSQC ([client/progs.inc:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L10) → [lib/csqcmodel/cl_model.qc:22](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/csqcmodel/cl_model.qc#L22))
**Applicable decisions** · [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md) · [ADR-0019](../../decisions/ADR-0019-legacy-compatibility-mode.md)

### 139. `lib/csqcmodel/interpolate.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC uses pre-valid/valid flags, opt-in origin/angles/velocity and reset on teleport. Native interpolation offers Note, Reset and Sample with optional extrapolation.

**Open** · Check angle wrap, flags and first sample after packet loss.

**Sources** · [lib/csqcmodel/interpolate.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/csqcmodel/interpolate.qh) · [lib/csqcmodel/cl_model.qc:286](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/csqcmodel/cl_model.qc#L286) · [SnapshotInterpolation.cs:156](../../../src/VortexArena.Net/SnapshotInterpolation.cs#L156)
**Include routes** · CSQC ([client/progs.inc:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L10) → [lib/csqcmodel/cl_model.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/csqcmodel/cl_model.qc#L26))

### 140. `lib/csqcmodel/net.qh`

**Disposition** · architecture-replaced · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC uses a linked model message ID. Native receives its own snapshot schema and legacy runs compatible CSQC in a separate host.

**Open** · Compare field coverage and linked lifetime separately from wire IDs.

**Sources** · [lib/csqcmodel/net.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/csqcmodel/net.qh) · [lib/csqcmodel/cl_model.qc:223](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/csqcmodel/cl_model.qc#L223) · [ClientNet.cs:1801](../../../game/net/ClientNet.cs#L1801)
**Include routes** · CSQC ([client/progs.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L5) → [client/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/_mod.inc#L6) → [client/main.qc:29](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/main.qc#L29) → [common/net_linked.qh:67](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/net_linked.qh#L67)) · SVQC ([server/progs.inc:22](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L22) → [lib/warpzone/server.qc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/warpzone/server.qc#L6) → [common/net_linked.qh:67](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/net_linked.qh#L67))
**Applicable decisions** · [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md) · [ADR-0019](../../decisions/ADR-0019-legacy-compatibility-mode.md)

### 141. `lib/csqcmodel/sv_model.qh`

**Disposition** · architecture-replaced · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC generates duplicate properties for dirty checks then links/unlinks through the CSQC model sender. Native builds typed snapshots and encodes deltas.

**Open** · Compare reparenting, viewloc, animation and removal/reappearance.

**Sources** · [lib/csqcmodel/sv_model.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/csqcmodel/sv_model.qh) · [common/csqcmodel_settings.qh:107](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/csqcmodel_settings.qh#L107) · [ServerNet.cs:3223](../../../game/net/ServerNet.cs#L3223)
**Include routes** · SVQC ([server/progs.inc:18](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L18) → [lib/csqcmodel/sv_model.qc:22](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/csqcmodel/sv_model.qc#L22))
**Applicable decisions** · [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md)

### 142. `lib/matrix/command.qc`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Dedicated operator console and loopback checks](../matrix/VERIFICATION.md)

The dedicated console accepts mx and sv_cmd mx controls for the account, token, room, polls, typing, display name and messages.

**Open** · Interoperability was exercised against a loopback homeserver.

**Sources** · [lib/matrix/command.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/matrix/command.qc) · [lib/matrix/command.qc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/matrix/command.qc#L5) · [Commands.Matrix.cs:8](../../../src/VortexArena.Server/Commands.Matrix.cs#L8)
**Include routes** · CSQC ([client/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L1) → [lib/_all.inc:160](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L160) → [lib/matrix/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/matrix/_mod.inc#L2)) · MENUQC ([menu/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L1) → [lib/_all.inc:160](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L160) → [lib/matrix/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/matrix/_mod.inc#L2)) · SVQC ([server/progs.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L9) → [lib/_all.inc:160](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L160) → [lib/matrix/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/matrix/_mod.inc#L2))

### 143. `lib/matrix/matrix.qc`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Dedicated Matrix transport and room lifecycle](../matrix/VERIFICATION.md)

Native handles asynchronous room operations and cursor-based polling. The loopback fixture exercised both polling modes, retry and room changes.

**Open** · A public federated homeserver was outside the runtime check.

**Sources** · [lib/matrix/matrix.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/matrix/matrix.qc) · [lib/matrix/command.qc:32](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/matrix/command.qc#L32) · [MatrixChatBridge.cs:306](../../../src/VortexArena.Server/MatrixChatBridge.cs#L306)
**Include routes** · CSQC ([client/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L1) → [lib/_all.inc:160](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L160) → [lib/matrix/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/matrix/_mod.inc#L3)) · MENUQC ([menu/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L1) → [lib/_all.inc:160](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L160) → [lib/matrix/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/matrix/_mod.inc#L3)) · SVQC ([server/progs.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L9) → [lib/_all.inc:160](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L160) → [lib/matrix/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/matrix/_mod.inc#L3))

### 144. `lib/matrix/matrix.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Dedicated Matrix transport and room lifecycle](../matrix/VERIFICATION.md)

Credentials and room selection are scoped to the dedicated world. The native callback delivers text as server chat.

**Open** · The loopback test verified token clearing and teardown on rotation.

**Sources** · [lib/matrix/matrix.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/matrix/matrix.qh) · [lib/matrix/matrix.qc:69](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/matrix/matrix.qc#L69) · [Commands.Matrix.cs:12](../../../src/VortexArena.Server/Commands.Matrix.cs#L12)
**Include routes** · CSQC ([client/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L1) → [lib/_all.inc:160](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L160) → [lib/matrix/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/matrix/_mod.inc#L3) → [lib/matrix/matrix.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/matrix/matrix.qc#L1)) · MENUQC ([menu/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L1) → [lib/_all.inc:160](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L160) → [lib/matrix/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/matrix/_mod.inc#L3) → [lib/matrix/matrix.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/matrix/matrix.qc#L1)) · SVQC ([server/progs.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L9) → [lib/_all.inc:160](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L160) → [lib/matrix/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/matrix/_mod.inc#L3) → [lib/matrix/matrix.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/matrix/matrix.qc#L1))

### 145. `lib/warpzone/anglestransform.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC handles pitch conventions, composition/inversion and model/view angles. Native uses a typed rigid transformation.

**Open** · Compare vertical normals, minimized roll and multi-zone composition.

**Sources** · [lib/warpzone/anglestransform.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/warpzone/anglestransform.qh) · [lib/warpzone/anglestransform.qc:136](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/warpzone/anglestransform.qc#L136) · [Warpzone.cs:22](../../../src/VortexArena.Common/Gameplay/MapObjects/Warpzone.cs#L22)
**Include routes** · CSQC ([client/progs.inc:14](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L14) → [lib/warpzone/anglestransform.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/warpzone/anglestransform.qc#L1)) · SVQC ([server/progs.inc:20](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L20) → [lib/warpzone/anglestransform.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/warpzone/anglestransform.qc#L1))
**Applicable decisions** · [ADR-0010](../../decisions/ADR-0010-determinism-and-numerics.md)

### 146. `lib/warpzone/common.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC traces, toss, radius and particles accumulate transforms across zones, with callback and explicit entry support. Native trace and radius have typed bridges.

**Open** · Compare entry restrictions, trigger intersection, hit fraction and multi-hop reference state.

**Sources** · [lib/warpzone/common.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/warpzone/common.qh) · [server/weapons/tracing.qc:49](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/weapons/tracing.qc#L49) · [WarpzoneRadiusQuery.cs:135](../../../src/VortexArena.Common/Gameplay/Warpzone/WarpzoneRadiusQuery.cs#L135)
**Include routes** · CSQC ([client/progs.inc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L15) → [lib/warpzone/common.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/warpzone/common.qc#L1)) · SVQC ([server/progs.inc:21](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L21) → [lib/warpzone/common.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/warpzone/common.qc#L1))

### 147. `lib/warpzone/mathlib.qc`

**Disposition** · architecture-replaced · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC implements floating classification, nextafter, logs, remainder and special functions. Native uses managed and gameplay-specific math.

**Open** · Trace individual active calls and verify subnormals, signed zero and approximation accuracy.

**Sources** · [lib/warpzone/mathlib.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/warpzone/mathlib.qc) · [lib/warpzone/mathlib.qc:229](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/warpzone/mathlib.qc#L229) · [QMath.cs:17](../../../src/VortexArena.Common/Math/QMath.cs#L17)
**Include routes** · CSQC ([client/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L1) → [lib/_all.inc:106](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L106)) · MENUQC ([menu/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L1) → [lib/_all.inc:106](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L106)) · SVQC ([server/progs.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L9) → [lib/_all.inc:106](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L106))
**Applicable decisions** · [ADR-0010](../../decisions/ADR-0010-determinism-and-numerics.md)

### 148. `lib/warpzone/mathlib.qh`

**Disposition** · architecture-replaced · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC header exposes C99-style providers and constants. Managed equivalence depends on consumer, especially special values.

**Open** · Identify users reached through math macros and compare tie rounding and NaN.

**Sources** · [lib/warpzone/mathlib.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/warpzone/mathlib.qh) · [lib/warpzone/mathlib.qh:85](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/warpzone/mathlib.qh#L85) · [QMath.cs:17](../../../src/VortexArena.Common/Math/QMath.cs#L17)
**Include routes** · CSQC ([client/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/client/progs.inc#L1) → [lib/_all.inc:106](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L106) → [lib/warpzone/mathlib.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/warpzone/mathlib.qc#L1)) · MENUQC ([menu/progs.inc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/menu/progs.inc#L1) → [lib/_all.inc:106](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L106) → [lib/warpzone/mathlib.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/warpzone/mathlib.qc#L1)) · SVQC ([server/progs.inc:9](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L9) → [lib/_all.inc:106](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/_all.inc#L106) → [lib/warpzone/mathlib.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/warpzone/mathlib.qc#L1))
**Applicable decisions** · [ADR-0010](../../decisions/ADR-0010-determinism-and-numerics.md)

### 149. `server/chat.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC uses per-channel flood, spectator policy, team/tell formatting and nearest-location macros. Native Say dispatches typed player/null source and maintains channel state.

**Open** · Test mixed burst/length, nearest-location ties and warmup permissions.

**Sources** · [server/chat.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/chat.qh) · [server/chat.qc:27](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/chat.qc#L27) · [Chat.cs:88](../../../src/VortexArena.Server/Chat.cs#L88)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L5) → [server/chat.qc:1](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/chat.qc#L1))

### 150. `server/cheats.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC gates impulses, counts cheats and transfers drag across corpse copying. Native eligibility and commands have separate Sandbox drag behavior.

**Open** · Check corpse transfer, speedrun state and impulse/frame routing.

**Sources** · [server/cheats.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/cheats.qh) · [server/impulse.qc:399](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/impulse.qc#L399) · [Cheats.cs:68](../../../src/VortexArena.Server/Cheats.cs#L68)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L4) → [server/campaign.qc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/campaign.qc#L7))

### 151. `server/damage.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

Native centralizes damage through typed hooks. Stock flags and keys now opt into trigger damage explicitly and the general hurt and kill targets honor that field. The creature and non-creature regression cases exercised both cvar settings and attribution.

**Open** · Compare force-only hits, mirror scaling, death tags and callback ordering independently of trigger eligibility.

**Sources** · [server/damage.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/damage.qh) · [server/player.qc:234](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/player.qc#L234) · [DamageSystem.cs:37](../../../src/VortexArena.Common/Gameplay/Damage/DamageSystem.cs#L37)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L6) → [server/cheats.qc:26](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/cheats.qc#L26))

### 152. `server/gamelog.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC supplies console/file split and counter/prefix/suffix/format inputs. Native appends via the same settings and owns Init/Close lifecycle.

**Open** · Compare IPv6 addresses, timestamp format and close on restart.

**Sources** · [server/gamelog.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/gamelog.qh) · [server/gamelog.qc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/gamelog.qc#L15) · [GameLog.cs:72](../../../src/VortexArena.Server/GameLog.cs#L72)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L2) → [server/anticheat.qc:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/anticheat.qc#L10))

### 153. `server/hook.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC contract carries hook state bits, per-slot origins, environmental and tension cvars. Native weapon/offhand code handles actions and removal.

**Open** · Compare frozen pulling, stretch, crouchslide, nade timing and multiplayer origins.

**Sources** · [server/hook.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/hook.qh) · [server/hook.qc:103](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/hook.qc#L103) · [Hook.cs:554](../../../src/VortexArena.Common/Gameplay/Weapons/Hook.cs#L554)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L7) → [server/client.qc:63](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/client.qc#L63))

### 154. `server/intermission.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC tracks nextmap/exit, overrides and screenshot/audio inputs. Native starts a winner countdown and delegates rotation separately.

**Open** · Compare lastlevel/samelevel, screenshot/track and exit timing.

**Sources** · [server/intermission.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/intermission.qh) · [server/intermission.qc:405](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/intermission.qc#L405) · [Intermission.cs:59](../../../src/VortexArena.Server/Intermission.cs#L59)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L4) → [server/campaign.qc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/campaign.qc#L8))

### 155. `server/main.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC main sweeps contents/fall damage with elapsed server time, and declares safe removal/autopause. Native world uses a creature/projectile sweep and separate lifecycle paths.

**Open** · Check pause and vertical-only fall, water sounds and protected removal.

**Sources** · [server/main.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/main.qh) · [server/main.qc:360](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/main.qc#L360) · [GameWorld.cs:2779](../../../src/VortexArena.Server/GameWorld.cs#L2779)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L2) → [server/anticheat.qc:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/anticheat.qc#L11))

### 156. `server/mapvoting.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC can first vote gametype then build map choices under cvar policy. Native exposes separate start paths and delayed state.

**Open** · Compare recent/suggested maps, screenshots, size with bots and reset after mode switch.

**Sources** · [server/mapvoting.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/mapvoting.qh) · [server/world.qc:1735](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/world.qc#L1735) · [MapVoting.cs:270](../../../src/VortexArena.Server/MapVoting.cs#L270)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L5) → [server/chat.qc:15](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/chat.qc#L15))

### 157. `server/matrix.qc`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison. [Bidirectional loopback chat](../matrix/VERIFICATION.md)

Native accepts room text from other senders and delivers it as world-source chat. A separate client received the message and the fixture verified self-echo filtering.

**Open** · The test covers plain text and original-compatible polling.

**Sources** · [server/matrix.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/matrix.qc) · [lib/matrix/matrix.qc:69](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/matrix/matrix.qc#L69) · [MatrixChatBridge.cs:328](../../../src/VortexArena.Server/MatrixChatBridge.cs#L328)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:19](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L19))

### 158. `server/player.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC contract carries default model/skin, spawn protection, corpse copying/animation/damage and clone list. Native responsibilities live in player/spawn/corpse paths.

**Open** · Compare default/team/personal model precedence, gib threshold and delayed corpse think transfer.

**Sources** · [server/player.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/player.qh) · [server/player.qc:183](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/player.qc#L183) · [PlayerFrameLogic.cs:480](../../../src/VortexArena.Server/PlayerFrameLogic.cs#L480)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L6) → [server/cheats.qc:29](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/cheats.qc#L29))

### 159. `server/race.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC publishes checkpoint/ranking state, fractional time, records and speed awards. Native maintains typed race state and records feeding scoreboard.

**Open** · Verify UID rename, onlyself checkpoint updates, rankings caps and stale record deletion.

**Sources** · [server/race.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/race.qh) · [server/race.qc:332](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/race.qc#L332) · [Race.cs:307](../../../src/VortexArena.Common/Gameplay/GameTypes/Race.cs#L307)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L6) → [server/cheats.qc:30](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/cheats.qc#L30))

### 160. `server/bot/default/navigation.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC has 32 goals, temporary item-edge costs, teleport overlap and progress time. Native uses typed route and graph state.

**Open** · Compare unreachable cost 999, dynamic goals, jetpack and submerged branches.

**Sources** · [server/bot/default/navigation.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/bot/default/navigation.qh) · [server/bot/default/navigation.qc:1430](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/bot/default/navigation.qc#L1430) · [BotNavigation.cs:148](../../../src/VortexArena.Server/Bot/BotNavigation.cs#L148)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:32](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L32) → [server/bot/_mod.inc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/bot/_mod.inc#L4) → [server/bot/default/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/bot/default/_mod.inc#L3) → [server/bot/default/bot.qc:20](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/bot/default/bot.qc#L20))

### 161. `server/bot/default/waypoints.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC default graph carries 32 sorted outgoing links and eight hardwired refs plus version stamp/symmetry state. Native loads graph and caches into typed link lists.

**Open** · Check version 1.04 invalidation and editor symmetry roundtrip.

**Sources** · [server/bot/default/waypoints.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/bot/default/waypoints.qh) · [server/bot/default/waypoints.qc:1837](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/bot/default/waypoints.qc#L1837) · [Waypoint.cs:1351](../../../src/VortexArena.Server/Bot/Waypoint.cs#L1351)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L7) → [server/client.qc:50](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/client.qc#L50))

### 162. `server/command/banning.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC macros consume optional time/mask until zero and use the remaining unparsed reason. Native has typed ban commands/persistence and channel scopes.

**Open** · Compare zero parsing, reason quoting, ID mode, trusted sync and user notifications.

**Sources** · [server/command/banning.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/command/banning.qh) · [server/command/banning.qc:33](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/command/banning.qc#L33) · [Bans.cs:298](../../../src/VortexArena.Server/Bans.cs#L298)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L7) → [server/client.qc:55](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/client.qc#L55))

### 163. `server/command/radarmap.qh`

**Disposition** · stock-disabled · inventory `unresolved`.
**Evidence quality** · Static build/configuration gate.

QC command exists, but default compilation supplies a printing stub. RADARMAP builds opt-in 512×512 generation. Native radar consumes images and no corresponding generation command was located.

**Open** · Compare map generation only with explicit opt-in build.

**Sources** · [server/command/radarmap.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/command/radarmap.qh) · [server/command/sv_cmd.qc:1802](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/command/sv_cmd.qc#L1802) · [RadarPanel.cs:238](../../../game/hud/RadarPanel.cs#L238)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:33](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L33) → [server/command/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/command/_mod.inc#L5) → [server/command/sv_cmd.qc:21](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/command/sv_cmd.qc#L21))

### 164. `server/command/sv_cmd.qh`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison. [Three program exports and stock comparison](../local-commands/README.md)

QC exposes delayed shuffle, record deletion and command alias generation. Native implements all three. Dedicated and listen servers produced identical native alias files. Server, client and menu each export their own registered commands.

**Open** · The stock client alias program was traced in source. The saved original exports cover the dedicated server and menu. Detailed reset timing and record indices remain separate checks.

**Sources** · [server/command/sv_cmd.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/command/sv_cmd.qh) · [server/command/vote.qc:368](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/command/vote.qc#L368) · [Commands.AliasDump.cs:22](../../../src/VortexArena.Server/Commands.AliasDump.cs#L22)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:30](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L30) → [server/world.qc:36](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/world.qc#L36))

### 165. `server/command/vote.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC frame dispatch counts eligible selections and coordinates ready/reset state. Native controller handles expiry, counts and selection with separate world reset.

**Open** · Compare abstention, initial count rules, quit during voting and warmup command restrictions.

**Sources** · [server/command/vote.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/command/vote.qh) · [server/world.qc:1727](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/world.qc#L1727) · [VoteController.cs:401](../../../src/VortexArena.Server/VoteController.cs#L401)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L7) → [server/client.qc:58](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/client.qc#L58))

### 166. `server/compat/halflife.qc`

**Disposition** · architecture-replaced · inventory `unresolved`.
**Evidence quality** · Static source comparison.

QC map dispatch registers inert Half-Life class handlers and declares three legacy-form empty functions. Native registry has no located matching stub list.

**Open** · Check unknown native entity retention vs inert QC edict on a fixture map.

**Sources** · [server/compat/halflife.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/compat/halflife.qc) · [server/compat/halflife.qc:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/compat/halflife.qc#L10) · [MapObjectsRegistry.cs:21](../../../src/VortexArena.Common/Gameplay/MapObjects/MapObjectsRegistry.cs#L21)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:34](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L34) → [server/compat/_mod.inc:2](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/compat/_mod.inc#L2))
**Applicable decisions** · [ADR-0007](../../decisions/ADR-0007-entity-model.md)

### 167. `server/compat/quake.qc`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC item_health maps flag 2 to Mega, Medium otherwise. Native registers that branch and Q1 armor/weapon aliases.

**Open** · Compare item_armorInv casing and spawned assets.

**Sources** · [server/compat/quake.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/compat/quake.qc) · [server/compat/quake.qc:20](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/compat/quake.qc#L20) · [ItemSpawnFuncs.cs:120](../../../src/VortexArena.Common/Gameplay/Items/ItemSpawnFuncs.cs#L120)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:34](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L34) → [server/compat/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/compat/_mod.inc#L3))

### 168. `server/compat/quake2.qc`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC maps jacket to Medium armor and invulnerability to Shield. Native alias table implements the same names.

**Open** · Check imported map powerup gates and spawnflags.

**Sources** · [server/compat/quake2.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/compat/quake2.qc) · [server/compat/quake2.qc:11](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/compat/quake2.qc#L11) · [ItemSpawnFuncs.cs:113](../../../src/VortexArena.Common/Gameplay/Items/ItemSpawnFuncs.cs#L113)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:34](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L34) → [server/compat/_mod.inc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/compat/_mod.inc#L4))

### 169. `server/compat/quake3.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC optionally scales count then rounds shots times ammo-per-shot before spawning the mapped resource. Native uses an ammo remapper before common spawn.

**Open** · Check rounding ties, zero count, ammo-less definitions, hitbox switch and Q3 variants.

**Sources** · [server/compat/quake3.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/compat/quake3.qh) · [server/compat/quake3.qc:55](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/compat/quake3.qc#L55) · [ItemSpawnFuncs.cs:278](../../../src/VortexArena.Common/Gameplay/Items/ItemSpawnFuncs.cs#L278)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L7) → [server/client.qc:59](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/client.qc#L59))

### 170. `server/compat/wop.qc`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

WoP aliases forward weapon/ammo, pad power, shields and Speed/Invisibility. QC revival/jumper are handled by buffs elsewhere.

**Open** · Check all aliases and revival/jump integration on an imported map.

**Sources** · [server/compat/wop.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/compat/wop.qc) · [server/compat/wop.qc:39](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/compat/wop.qc#L39) · [ItemSpawnFuncs.cs:126](../../../src/VortexArena.Common/Gameplay/Items/ItemSpawnFuncs.cs#L126)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:34](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L34) → [server/compat/_mod.inc:6](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/compat/_mod.inc#L6))

### 171. `server/mutators/loader.qc`

**Disposition** · architecture-replaced · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC reads the current mode's mutator names in order and adds the first match for each. Native activates a typed mode and reconciles configured mutators.

**Open** · Check unknown names, duplicate ordering and reload of same mode.

**Sources** · [server/mutators/loader.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/mutators/loader.qc) · [server/mutators/loader.qc:8](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/mutators/loader.qc#L8) · [MutatorActivation.cs:24](../../../src/VortexArena.Common/Gameplay/Mutators/MutatorActivation.cs#L24)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:36](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L36) → [server/mutators/_mod.inc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/mutators/_mod.inc#L3))
**Applicable decisions** · [ADR-0003](../../decisions/ADR-0003-source-generators.md)

### 172. `server/pathlib/pathlib.qh`

**Disposition** · stock-disabled · inventory `unresolved`.
**Evidence quality** · Static build/configuration gate.

QC defines grid lists, water costs, edge flags and search callbacks. Mobile turbine A* paths require undefined stock EWheel/Walker flags. Native follows authored checkpoints.

**Open** · Trace custom flag enablement and any independent A* consumer.

**Sources** · [server/pathlib/pathlib.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/pathlib/pathlib.qh) · [common/turrets/turret/ewheel.qc:36](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/turrets/turret/ewheel.qc#L36) · [EWheelTurret.cs:227](../../../src/VortexArena.Common/Gameplay/Turrets/EWheelTurret.cs#L227)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:37](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L37) → [server/pathlib/_mod.inc:4](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/pathlib/_mod.inc#L4) → [server/pathlib/expandnode.qc:3](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/pathlib/expandnode.qc#L3))

### 173. `server/weapons/hitplot.qc`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

Native firing samples the trace and projects all eight target corners inside a forced rewind, restores the target, and writes normalized XY, depth and stock weapon ID. The Blaster regression uses injected player history.

**Open** · A paired shot under real network latency has not been recorded.

**Sources** · [server/weapons/hitplot.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/weapons/hitplot.qc) · [server/weapons/tracing.qc:69](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/weapons/tracing.qc#L69) · [HitPlots.cs:54](../../../src/VortexArena.Server/HitPlots.cs#L54)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:38](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L38) → [server/weapons/_mod.inc:5](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/weapons/_mod.inc#L5))

### 174. `server/weapons/hitplot.qh`

**Disposition** · counterpart · inventory `unresolved`.
**Evidence quality** · Static source comparison.

Native opens a match-local plot for all clients or exact selected addresses, closes it on detach or world teardown, and writes to game user data. A native loopback produced separate local and remote files.

**Open** · The live run covers file selection and lifecycle. Rotating maps during a lagged shot remains unmeasured.

**Sources** · [server/weapons/hitplot.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/weapons/hitplot.qh) · [common/state.qc:60](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/state.qc#L60) · [HitPlots.cs:17](../../../src/VortexArena.Server/HitPlots.cs#L17)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L7) → [server/client.qc:78](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/client.qc#L78))

### 175. `server/weapons/selection.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC cycles from user priority, direction, impulse, missing/ammo gates and complaint state. Native has registered ordering and typed firing slots.

**Open** · Compare last-weapon fallback, dual selection and complaint throttle.

**Sources** · [server/weapons/selection.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/weapons/selection.qh) · [server/weapons/selection.qc:122](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/weapons/selection.qc#L122) · [WeaponOrder.cs:115](../../../src/VortexArena.Common/Gameplay/Weapons/WeaponOrder.cs#L115)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L7) → [server/client.qc:79](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/client.qc#L79))

### 176. `server/weapons/weaponstats.qc`

**Disposition** · counterpart-with-delta · inventory `unresolved`.
**Evidence quality** · Static source comparison. [Native per-map export and focused tests](../registry/sv-weaponstats.yaml)

Native now accumulates hits, health and armor damage, and kills using original weapon IDs and bot/human strata. A live two-map server flushed exactly one report per map to the same local file.

**Open** · Native now tries quoted local paths and stdout in the configured order. HTTP delivery remains open. UTC and native config/build metadata can change the timestamp or checksum.

**Sources** · [server/weapons/weaponstats.qc](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/weapons/weaponstats.qc) · [server/world.qc:1433](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/world.qc#L1433) · [sv-weaponstats.yaml:27](../../../planning/parity/registry/sv-weaponstats.yaml#L27)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:38](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L38) → [server/weapons/_mod.inc:10](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/weapons/_mod.inc#L10))

### 177. `server/weapons/weaponstats.qh`

**Disposition** · counterpart-with-delta · inventory `unresolved`.
**Evidence quality** · Static source comparison. [Native per-map export and focused tests](../registry/sv-weaponstats.yaml)

Native now accumulates hits, health and armor damage, and kills using original weapon IDs and bot/human strata. A live two-map server flushed exactly one report per map to the same local file.

**Open** · Native now tries quoted local paths and stdout in the configured order. HTTP delivery remains open. UTC and native config/build metadata can change the timestamp or checksum.

**Sources** · [server/weapons/weaponstats.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/weapons/weaponstats.qh) · [server/player.qc:427](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/player.qc#L427) · [sv-weaponstats.yaml:27](../../../planning/parity/registry/sv-weaponstats.yaml#L27)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:20](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L20) → [server/player.qc:38](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/player.qc#L38))

### 178. `server/weapons/weaponsystem.qh`

**Disposition** · counterpart · inventory `native-counterpart`.
**Evidence quality** · Static source comparison.

QC tracks cooldown per weapon/slot, clip state and scheduled Think with up to two weapon tics per server frame. Native per-slot fire driver runs from GameWorld.

**Open** · Compare low-FPS timing, switching, dual slot independence and post-death flash.

**Sources** · [server/weapons/weaponsystem.qh](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/weapons/weaponsystem.qh) · [server/weapons/weaponsystem.qc:470](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/weapons/weaponsystem.qc#L470) · [WeaponFireDriver.cs:64](../../../src/VortexArena.Common/Gameplay/Weapons/WeaponFireDriver.cs#L64)
**Include routes** · SVQC ([server/progs.inc:13](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/progs.inc#L13) → [server/_mod.inc:7](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/_mod.inc#L7) → [server/client.qc:81](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/server/client.qc#L81))
