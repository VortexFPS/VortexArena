# Unmapped QuakeC audit

The coverage ledger listed **631 unmapped files and 17,070 physical lines** at Xonotic data revision `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`. The [file ledger](dispositions.json) contains one disposition for every file, ordered by parent directory and filename. The [comparison rules](comparisons.json) record the source and decision anchors behind those dispositions. The initial inventory recorded 65 unresolved files and 113 with candidate native counterparts. Later comparisons identified native counterparts for four Matrix files, the debug header, the view-location implementation, the rubble list and eviction helpers, and the local music-driven map animation. The view-location header contains declarations. The [S1 review](S1-REVIEW.md) follows all 178 in the same order, with source witnesses, open dimensions and [focused runtime evidence](S1-VERIFICATION.md).

| QC directory | Files | Lines | Routing or empty | Declarations | Architecture replaced | Native counterpart | Documented gap | Inactive or no stock caller found | Unresolved |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| `client` | 22 | 347 | 18 | 0 | 0 | 4 | 0 | 0 | 0 |
| `common` | 425 | 6,400 | 278 | 25 | 8 | 96 | 0 | 0 | 18 |
| `ecs` | 19 | 196 | 9 | 1 | 9 | 0 | 0 | 0 | 0 |
| `lib` | 75 | 6,312 | 14 | 3 | 31 | 12 | 0 | 0 | 15 |
| `server` | 90 | 3,815 | 38 | 8 | 3 | 22 | 0 | 11 | 8 |
| **Total** | **631** | **17,070** | **357** | **37** | **51** | **134** | **0** | **11** | **41** |

## Reading the dispositions

- **Build routing** covers 353 files consisting of include, pragma and conditional scaffolding after comment removal. A route can lead to live code. The ledger records its include targets and guards. The four **empty** files contain no directives or code.
- **Declarations** identifies files whose own contents supply contracts. Their implementations and users can still affect gameplay. Constants with initializers, inline bodies and macro expansions are evaluated separately from this category.
- **Architecture replaced** covers files whose mechanisms are handled elsewhere in native code, such as QC registration, typed entity state and DarkPlaces wire dispatch. Consumer semantics can still differ. The native stack and the separately hosted legacy CSQC VM have distinct entry points. [Rewrite strategy](../../decisions/ADR-0001-rewrite-strategy.md) · [Source generators](../../decisions/ADR-0003-source-generators.md) · [Entity model](../../decisions/ADR-0007-entity-model.md) · [Protocol boundary](../../decisions/ADR-0011-protocol-ecosystem-boundary.md) · [Legacy mode](../../decisions/ADR-0019-legacy-compatibility-mode.md)
- **Native counterpart** identifies concrete C# code for the same responsibility. This disposition makes no claim about complete behavioral parity. **Documented gap** is backed by a source comment and an existing discrepancy record.
- **Stock inactive** covers six files. Three have bodies compiled out by a literal or default build condition. Three contain compiled A* callbacks whose observed entry points are gated by disabled stock flags. **Compiled, no stock caller found** covers five files with nominally compiled code or contracts where the examined chain did not reach a stock runtime entry.
- **Unresolved** means the observed code, caller or metadata requires more evidence to assign a narrower disposition. Read the rule's assessment for the unresolved comparison.

The [fixed cohort](cohort.tsv) preserves the exact upstream paths and line counts at the start of this review. New registry citations can remove a file from the live [coverage inventory](../COVERAGE.md). It remains in the ordered review so that fixes and regressions can be traced to the original finding.

## Caller and macro findings

QC anchors below are relative to the pinned checkout’s `qcsrc/`. The ledger resolves them to line numbers.

| Area | Evidence and parity implication |
|---|---|
| Menu descriptions | The mode `describe` methods in the inventory are under `MENUQC`. They reach the menu through `e.describe(e)` in `menu/xonotic/dialog_media_guide.qc:77`. Native mode classes establish the rules counterpart. Nine Simplified Chinese descriptions were checked in both framed Guides at 1100 × 700. The remaining descriptions and their presentation have individual entries in the ordered review. [Paired Guide selections](../guide-gametypes/README.md) [Native guide](../../../game/menu/dialogs/DialogMediaGuide.cs) |
| Powerups and effects | Invisibility, Shield and Speed headers register items and status effects. The spawn macros in these headers let maps invoke aliases without a direct QC caller. Damage text uses `REGISTER_NET_LINKED` with a `NET_HANDLE` receiver. Configuration, ordering, payload fields and presentation still need their own checks. [Native item spawns](../../../src/VortexArena.Common/Gameplay/Items/ItemSpawnFuncs.cs) · [Native status effects](../../../src/VortexArena.Common/Gameplay/StatusEffects.cs) · [Damage text renderer](../../../game/client/DamageTextLayer.cs) |
| View location | The movement caller is in `ecs/systems/physics.qc:45`. The camera call is inside the `CSQCPLAYER_HOOK_POSTCAMERASETUP` macro at `common/csqcmodel_settings.qh:91`, invoked by `lib/csqcmodel/cl_player.qc:664` after camera setup. Paired runs verified exact brush clipping, rail entry and exit, and the followed player’s camera on a server-only entity replacement. The owner’s free-aim cursor matched at two sampled positions. Both native clients held the snapped yaw on reentry, while stock exposed an intermediate crossing frame. Reticle fades and snapshot latency remain open. [Free-aim and camera trials](../view-location/FREEAIM.md) [Paired camera evidence](../view-location/README.md) [Native map wiring](../../../src/VortexArena.Common/Gameplay/MapObjects/ViewLocation.cs) · [Native movement](../../../src/VortexArena.Common/Physics/PlayerPhysics.cs) |
| Turret path library | `server/_mod.inc:37` includes the library. EWheel and Walker call `pathlib_astar` only under flags whose definitions are commented out. Native mobile turrets walk checkpoint chains. The separate waypoint search has no established stock entry. The shared `location_isok` helper has a cross-file call through `beamsweep`, but the search found no activation of `steerlib_beamsteer`. [Native EWheel](../../../src/VortexArena.Common/Gameplay/Turrets/EWheelTurret.cs) · [Native Walker](../../../src/VortexArena.Common/Gameplay/Turrets/WalkerTurret.cs) · [Native bot navigation](../../../src/VortexArena.Server/Bot/BotNavigation.cs) |
| Join notices, hitplots and weapon statistics | Source establishes callers for all three. Join notices are gated by `sv_join_notices`. Hitplot analysis runs from player weapon tracing, while client-state attach/detach opens and closes the file when the configured gate allows it. Weapon statistics are fed by damage and kills, with output gated by `sv_weaponstats_file`. The pinned server config defaults leave each output disabled. Native loopback players received separate notices. The listen host and remote client produced separate hitplot files, and the native server wrote one local weaponstats report per map. [Join notice verification](S1-VERIFICATION.md#two-client-join-notices) · [Hitplot tests](../../../tests/VortexArena.Tests/HitPlotTests.cs) · [Weaponstats verification](../registry/sv-weaponstats.yaml) |
| Transient models and rubble | Source tracing located the active Fireball call and two CTF references disabled by explicit preprocessor guards. Paired shooter and observer runs exercised the native and stock Fireball sphere on impact, timeout and self-damage. [Captured frames and timing](../fireball-sphere/README.md) The rubble helpers enforce oldest-first eviction for casings, gibs and owner-scoped Electro orbs. An ordinary lethal shotgun blast placed gibs ahead of the shell in the stock observer’s rubble list. Both native peers received the model splash before the shell. Electro tracks launch order per owner. [Casing and gib evidence](../rubble-limits/network-order.md) · [Fireball discrepancy](../registry/weapon-fireball.yaml) · [Native casings](../../../game/client/ShellCasings.cs) · [Native gibs](../../../game/client/ModelGibs.cs) |
| Music-synchronized map objects | `common/mapobjects/bgmscript.qh` supplies the ADSR fields used by model and particle entities. Native reads the map fields and binds playback to both models and particle nodes on a listen host. Courtfun exercised all 36 emitters. Paired envelope timing and remote replication remain open. [Runtime evidence](../bgmscript/README.md) [Native models](../../../game/client/MapBgmModels.cs) · [Native particles](../../../game/client/MapParticleEmitters.cs) |
| Debug and compatibility | `ENABLE_DEBUGDRAW` and `ENABLE_DEBUGTRACE` default to 0. Native local entity and waypoint labels were verified in the graphical listen host. Stock also broadcasts the temporary labels to connected peers. [Paired label evidence](../debug-labels/README.md) The Half-Life compatibility file has inert map spawn handlers that can be invoked by classname. Q1, Q2, Q3 and WoP remaps have a native comparison surface, with variant flags and aliases still to check. [Native console](../../../game/console/ConsoleState.cs) · [Native remaps](../../../src/VortexArena.Common/Gameplay/MapObjects/CompatRemaps.cs) |

## Evidence and verification

Each entry records the pinned file hash and physical line count, source shape, possible include chain per stock build target, line anchors, and comparison IDs. The symbol inventory distinguishes function bodies, prototypes, macro definitions and macro invocations. A partial set of known expansions adds generated registration keys and handler names. Line-based references distinguish lexical call syntax, tokens, macro continuations and preprocessor references. Build inclusion and lexical call syntax provide leads. Runtime reachability requires an actual dispatch or caller chain.

The scanner follows literal includes from the three stock program roots. It resolves the program selectors, literal gates and the documented default-off flags used in these comparisons. Unmodeled preprocessor expressions remain possible. It does not run qcc, expand arbitrary macros, resolve dynamic dispatch in general, or execute gameplay. Where those limits matter, the comparison records a concrete traced chain or leaves the claim open.

Run the standard-library verifier from the repository root with the curated checkout available.

```sh
python3 tools/parity-unmapped-audit.py --check --base /tmp/vortex-parity-base
```

The verifier checks the QC commit and source state, file count and line counts, source hashes, exact comparison anchor text and regenerated ledger. It reads the existing coverage ledger, native code, ADRs and cited discrepancy records. S1 adds a separate manifest and preserves the historical inventory categories. Run the S1 verifier against the same checkout.

```sh
python3 tools/parity-s1-review.py --check --base /tmp/vortex-parity-base
```

Generate an updated S1 manifest by omitting `--check`. The companion generator verifies the selected source hashes, source witnesses, include routes and directory/file ordering.
