# Superweapons HUD verification

The native Powerups panel hides the Superweapons row when the unlimited item bit is set, including when the ordinary bit and an active status are also present. Finite pickups still show the remaining time and use the HUD skin's `hud_progressbar_superweapons_color`. The [original Stormkeep comparison](superweapons-hud.md) records the pre-change NIX HUD.

## Source trace

- Pinned Xonotic at `863cd3e8456e18f577f7fe5fef857ff55ae62cc0` sets bit 1 in the NIX `PlayerSpawn` hook and adds it to the InstaGib start items with the Vaporizer. The common spawn path applies Superweapon status when the starting weapon set includes a superweapon. The ordinary bit is set by `player_powerups` for eligible holders outside InstaGib, including unlimited holders of a superweapon. Ordinary map superweapons get their balance duration in `server/weapons/spawning.qc`, and `server/items/items.qc` applies the status on touch.
- The pinned Powerups HUD runs the status-effects hook and calls `Superweapon.m_tick` for an active effect. In gameplay it requires `IT_SUPERWEAPON` (bit 11) in `STAT(ITEMS)` and suppresses the row whenever `IT_UNLIMITED_SUPERWEAPONS` (bit 1) is set. The finite row uses the clamped time remaining, the skin tint and a nominal 30-second bar lifetime. See `qcsrc/common/mutators/mutator/status_effects/status_effect/superweapons.qc` in the pinned checkout.
- The native NIX spawn hook sets the unlimited bit. InstaGib's start-items hook hands the common spawn path a Vaporizer and the unlimited bit, and the resulting loadout carries a Superweapon status. The ordinary Fireball pickup applies the timed status on touch. `PlayerFrameLogic.PlayerPowerups` sets the ordinary bit and strips the expired weapon. [NIX](../../../src/VortexArena.Common/Gameplay/Mutators/NixMutator.cs), [InstaGib](../../../src/VortexArena.Common/Gameplay/Mutators/InstagibMutator.cs), [spawn](../../../src/VortexArena.Common/Gameplay/Player/SpawnSystem.cs), [pickup](../../../src/VortexArena.Common/Gameplay/Items/ItemPickupRules.cs), [frame logic](../../../src/VortexArena.Server/PlayerFrameLogic.cs).
- The native listen HUD reads the player state directly. A remote HUD mirrors the owner inventory's item bits and the decoded status effects from the owner entity and uses the latest server time. Both routes feed the same panel row builder. [Native HUD feed](../../../game/net/NetGame.cs), [Powerups panel](../../../game/hud/PowerupsPanel.cs).

## Repeatable check

```sh
dotnet build VortexArena.csproj -c Debug
bash tests/fixtures/superweapons-hud/run.sh /path/to/godot-mono /path/to/gmqcc /tmp/vortex-parity-base
dotnet test tests/VortexArena.Tests/VortexArena.Tests.csproj --filter 'FullyQualifiedName~SuperweaponMapPickup_ArmsOrdinaryItemBitAndExpires|FullyQualifiedName~PrivateWorldBoot_AppliesLaunchOverridesBeforeMutatorsAndCheatPolicy|FullyQualifiedName~StatusEffect|FullyQualifiedName~OwnerInventoryTests'
python3 tools/check-parity-refs.py
```

The headless Godot fixture calls the same native row builder used by `DrawPanel`. It spawns players through the production server's spawn path and touches a registered Fireball pickup. It advances the render clock deterministically and injects the unlimited bit during the live pickup to exercise the overlap. The pinned arm compiles CSQC from the Git object and runs its `StatusEffects_tick`, `Superweapon_m_tick` and `addPowerupItem` in the production QuakeC VM with the same item and status inputs. The fixture checks generated rows before rasterization. [Fixture](../../../tests/fixtures/superweapons-hud/Probe.cs).

| Gameplay case | Superweapon item bits | Native and pinned row |
| --- | --- | --- |
| NIX spawn | `0x002` | Hidden |
| InstaGib spawn with active status | `0x002` | Hidden |
| Pickup status before ordinary bit | `0x000` | Hidden |
| Timed Fireball pickup | `0x800` | One finite row at 24s, then 16s |
| Unlimited overlaps a live timer | `0x802` | Hidden |
| Unlimited overlaps persistent status | `0x802` | Hidden |
| Unlimited clears while time remains | `0x800` | One finite row at 14s |
| Fireball expires | `0x000` | Hidden |

Both timed rows used the fixture's skin tint and a 30-second bar lifetime. The fixture also checked that native Jetpack and Fuel regenerator rows still display as infinite. The Debug build and all 12 focused xUnit tests passed. This machine ran the fixture and xUnit with `DOTNET_ROLL_FORWARD=Major` on .NET 10 because its installed runtime is newer than the projects' .NET 8 target.
