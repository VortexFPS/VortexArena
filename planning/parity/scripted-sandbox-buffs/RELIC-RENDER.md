# Vampire relic rendering

The original and native captures use the same Stormkeep fixture. The relic MD3, Vampire skin 2 and shader definitions also match byte for byte between the pinned Xonotic checkout and the native core pack. No art or content changed.

| Asset | SHA-256 |
|---|---|
| `models/relics/relic.md3` | `c62d60f8c1776eeea15b8e3cbca7b395450e6548fe402b2ed9d4123ca85eebc9` |
| `models/relics/relic.md3_2.skin` | `cff8fee90f2f9451505e1e25bf8e9550730bb810430da72d0522e0459d1e1651` |
| `scripts/relics.shader` | `c4280a7d1ec36a0c5fbc65c2fe0b270fe06e07c65055f3677ba2ce14587a5ef7` |

The raw MD3 names include the obsolete `relic/1` sign and `models/relic/…` ring and body paths. Both native render paths had bypassed the sidecar. Skin 2 maps the sign to the unlit additive autosprite, the six rings to a diffuse-lit `SRC_ALPHA, ONE` pass, and the body to a diffuse-lit surface. Native now builds both pickup and carrier with the existing skin-aware, autosprite-capable MD3 builder. The pickup rebuilds when its networked skin changes. The model lighting follows the pickup's fullbright flag, and the visible exterior carrier uses fullbright. The mixed materials share the same per-instance tint and glow slots. An intermediate run had logged a uniform-index conflict when placing autosprite and opaque materials on the same mesh.

The matched native capture before this change shows dark relic silhouettes. An intermediate live repeat shows the pickup's additive surfaces at the same owner camera point in the same Compatibility renderer. [Original pickup and carrier](captures/original/buff.jpg) · [Native before](captures/native-buff/buff.jpg) · [Native pickup with skin-aware passes](captures/relic-render-pickup/06-buff-before-owner.webp)

The 0.3 inactive pickup alpha flows into the existing model transparency path. The Godot 4.6.3 API bundled with the runtime documents that Compatibility and Mobile ignore `GeometryInstance3D.Transparency`. These recordings cannot establish whole-model alpha or DarkPlaces dynamic-light parity. The carrier's per-recipient alpha and invisible-enemy handling remain outside the material fix.

Run the buff-only command in [the capture README](README.md) with `--engine native --phase buff`. The console path waits for a server-facing acknowledgement before collecting frames. The focused tests check the shipped Vampire skin, additive ring blend, autosprite lighting and per-instance slot agreement. A separate wider test attempt reported a failure in the Swapper motion/view test, outside these renderer changes. [Focused test log](captures/relic-render-fixed/tests.txt)

The final repeat reached both fixture viewpoints and the owner touched the pickup. The guest sees the magenta Vampire sign above the carrier. The owner sees the Vampire HUD indicator at 58 seconds. The final logs contain no mixed-material uniform-index warnings. The owner's near-camera pickup surface can still look opaque in Compatibility. [Final native frames](captures/relic-render-fixed/buff.jpg) · [Timeline](captures/relic-render-fixed/timeline.jsonl) · [Owner log](captures/relic-render-fixed/owner.log) · [Guest log](captures/relic-render-fixed/guest.log) · [Server log](captures/relic-render-fixed/server.log) · [Command lines](captures/relic-render-fixed/commands.json) · [Input hashes](captures/relic-render-fixed/inputs.json)
