# Remote view-location brush clipping

The fixture appends a collision-only triangular prism to a temporary copy of Stormkeep's BSP. It preserves the BSP entity lump. Only the dedicated server profile mounts an external `maps/stormkeep.ent`. That replacement creates the rail, anchors, spawn and teleport targets. Alpha joins and Beta follows Alpha via `cmd spectate Alpha`. Both clients mount the same modified BSP without the replacement entities.

The brush is bounded by X 1088–1344, Y 128–384 and Z −24–112, cut by the plane X + Y ≤ 1472. At `1160 220 16`, Alpha's hull overlaps the brush and both cameras use the server-authored rail at X 1000. At `1300 320 16`, the hull still overlaps the model bounds but is outside the clipped half-space. The sequence ends after a second rail entry and a teleport beyond the model bounds to `1216 448 16`.

Pinned QuakeC calls `WarpZoneLib_ExactTrigger_Touch` on the server. The helper tests the bounds first, then temporarily links the trigger as `SOLID_BSP` for a stationary `MOVE_NOMONSTERS` trace. The DarkPlaces release engine selects the model and clips to its brush geometry for this trace. Authored mins/maxs and model-less trigger boxes use the bounds directly. [Pinned QC clip helper](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/lib/warpzone/common.qc#L98) · [DarkPlaces server trace](https://github.com/xonotic/darkplaces/blob/f244ef2525c9c018f1b077d49959df3c77ebc7b1/sv_phys.c#L525) · [DarkPlaces model clip](https://github.com/xonotic/darkplaces/blob/f244ef2525c9c018f1b077d49959df3c77ebc7b1/collision.c#L1793)

Native tests the same stationary player hull directly against the target brush through the engine services facade. It uses the existing clipper without changing the trigger's solidity or linking. The native spectator and the local player's prediction receive their active region from the accepted authoritative ENet snapshot. The snapshot contains the resolved anchors, so the client does not need the server's entity replacement or DarkPlaces' CSQC entity links. [ADR-0009](../../decisions/ADR-0009-engine-services-facade.md) · [ADR-0011](../../decisions/ADR-0011-protocol-ecosystem-boundary.md)

## Live results

| Run | Enter | Inside the AABB only | Reenter | Beyond the AABB |
| --- | --- | --- | --- | --- |
| Stock, Alpha and Beta | Rail | First-person | Rail | First-person |
| Native baseline, Beta following Alpha | Rail | Rail | — | — |
| Native with brush clipping, Alpha and Beta | Rail | First-person | Rail | First-person |

Both contact and exit propagated to the follower. The screenshot rows show Alpha first and Beta second. Columns follow the four teleports from left to right. The baseline image shows the two relevant follower checkpoints. [Stock replay](stock.webp) · [Native baseline follower](native-baseline-follow.webp) · [Native replay](native.webp)

Stock used the supplied June 20, 2023 SDL release with the pinned QC programs. The older engine logged `FixClientCvars` warnings during the connection handshake. Both clients completed signon, joined or followed, and rendered all four checkpoints.

## Reproduce

Run from the repository root. The stock run uses the SDL executable, SDL libraries and compiled programs described by [the stock setup](../warpzones/reproduce-remote.sh). It checks the QC checkout pin. The Godot build needs the locked 4.6.3 .NET runtime.

```sh
export GODOT_BIN=/path/to/Godot_v4.6.3-stable_mono_linux.x86_64
DOTNET_ROLL_FORWARD=Major dotnet build VortexArena.csproj --no-restore
xvfb-run -a -s '-screen 0 1600x950x24' planning/parity/view-location/reproduce.sh stock "$(mktemp -d)"
xvfb-run -a -s '-screen 0 1600x950x24' planning/parity/view-location/reproduce.sh native "$(mktemp -d)"
DOTNET_ROLL_FORWARD=Major dotnet test tests/VortexArena.Tests/VortexArena.Tests.csproj --no-restore \
  --filter 'FullyQualifiedName~ViewLocation|FullyQualifiedName~OrderedAuditGameplayTests|FullyQualifiedName~DarkPlacesTraceParityTests|FullyQualifiedName~DarkPlacesCollisionTests|FullyQualifiedName~BspCollisionTests'
```

The focused run passed 47 tests. The new fixture test checks two players overlapping a single brush, one player leaving into the empty AABB corner, independent exit, reentry, and the explicit box override with the Xonotic one-unit expansion.

Four follow-up parameter combinations exercised the sloping plane and the explicit map bounds with Xonotic and Quake III compatibility settings. Xonotic’s extra unit of hull padding admitted an adjacent player at the sloped face. The Quake III case kept that player outside.

[FREEAIM and combined snap follow-up](FREEAIM.md)
