# Duel powerups on map load

With `g_duel_with_powerups` at its default of zero, both dedicated servers kept small health and removed all eight authored powerup pickups. The old native filter checked three class names and let speed, invisibility, jetpack, fuel regeneration and the haste alias through. Five of the six initial native regression cases failed before the fix.

The Duel hook in Xonotic data commit `863cd3e8456e18f577f7fe5fef857ff55ae62cc0` tests `definition.instanceOfPowerup`. Native now reads the corresponding flag on the pickup that the map spawn function resolved. Compatibility aliases pass the same definition as their canonical pickup. [Pinned Duel hook](https://github.com/xonotic/xonotic-data.pk3dir/blob/863cd3e8456e18f577f7fe5fef857ff55ae62cc0/qcsrc/common/gametypes/gametype/duel/sv_duel.qc#L14) · [Native hook](../../../src/VortexArena.Common/Gameplay/GameTypes/Duel.cs) · [Definition flag](../../../src/VortexArena.Common/Gameplay/Items/PowerupItem.cs) · [Spawn order](../../../src/VortexArena.Common/Gameplay/Items/StartItem.cs)

| Stormkeep load | Duel powerups | Strength | Shield | Speed | Invisibility | Jetpack | Fuel regen | Small health |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Initial match | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 1 |
| First reload | 1 | 1 | 2 | 2 | 1 | 1 | 1 | 1 |
| Second reload | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 1 |
| Only the two aliases and health authored | 1 | 0 | 1 | 1 | 0 | 0 | 0 | 1 |

Each cell is the number of matching entities returned by the operator console. Stock and native produced identical counts on every load. The fourth load isolated `item_invincible` and `item_haste`. They spawned as Shield and Speed, and querying their alias names returned zero. Each host kept the same process across its four loads. The global and individual powerup settings stayed enabled. The eight native regression cases also exercised each map classname separately. The live run covers ordinary Duel map spawns. InstaGib’s Extra Life has additional mode-specific spawn rules. [Console observations and input hashes](observations.txt) · [Regression cases](../../../tests/VortexArena.Tests/FilterItemHookTests.cs)

## Reproduce

Build the native Debug host. Supply Godot 4.6.3 Mono, a checkout at the pin, and the existing stock fixture directory containing the released SDL executable and the compiled QC. The runner checks the checkout revision and the SHA-256 of the staged stock server program. It uses temporary profiles and the repository’s Stormkeep pack. It prints the temporary directory with both complete server logs. [Runner](reproduce.py)

```sh
dotnet build VortexArena.csproj --no-restore
python3 planning/parity/duel-powerups/reproduce.py \
  --godot /path/to/Godot_v4.6.3-stable_mono_linux.x86_64 \
  --stock /tmp/va-reviewed-stock --base /tmp/vortex-parity-base --port 30518
```
