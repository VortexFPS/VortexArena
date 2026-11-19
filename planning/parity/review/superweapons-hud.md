# Superweapons HUD

The [paired Stormkeep capture](superweapons-hud.webp) shows the original discrepancy in a live NIX match. The earlier native HUD drew an infinity glyph above the ammo and health panels. Pinned Xonotic at `863cd3e` left that space empty. The full frames are reduced equally, with a full-resolution crop of the HUD below each.

The pinned client reads `STAT(ITEMS)` in `common/mutators/mutator/status_effects/status_effect/superweapons.qc`. It normally requires `IT_SUPERWEAPON` (bit 11) to show a timed row and suppresses the row whenever `IT_UNLIMITED_SUPERWEAPONS` (bit 1) is set. Native now uses the same rule, including when both bits are set.

The owner selected the original hidden row for unlimited loadouts and retained the countdown for ordinary timed superweapons. The production panel fixture covered both flags, NIX, InstaGib and a Fireball pickup through expiry. [Post-change verification](superweapons-hud-verification.md)

Ordinary map pickups are independent of this choice. The registered Fireball pickup now schedules its normal initial appearance, grants the configured timed status and the ordinary item bit on touch, and loses the weapon at expiry. Timed HUD rows use the skin's `hud_progressbar_superweapons_color` and wait for the ordinary item bit before drawing.

## Threaded host verification

The native frame uses the default threaded listen-server architecture. Before the fix, the private store received launch overrides after world initialization. A normal `--host stormkeep --cvar g_nix 1` session therefore spawned with the default loadout. The private store now receives known overrides after stock config load and before the world activates mutators or snapshots cheat policy. The threaded capture shows NIX active at spawn. A focused private-world test also verifies that both NIX and the launch cheat policy are active during initialization.
