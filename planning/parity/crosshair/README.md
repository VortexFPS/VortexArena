# Normal crosshair parity

Directory-order row 3 in `../unmapped-audit/s1-review.txt` points to `client/hud/crosshair.qh`. This check uses the full Xonotic data pin `863cd3e8456e18f577f7fe5fef857ff55ae62cc0` with the original SDL engine and the compiled client, server and menu QC from that pin. The native client is Godot 4.6.3 Mono. Both use a fresh profile and the same Stormkeep BSP with a one-spawn server entity override and a Vortex-only DM loadout.

## Source trace

| Behavior | Pinned Xonotic | Native |
| --- | --- | --- |
| Defaults and suppression | `crosshair.qh:9–59` declares the cvars. `crosshairs.cfg:6–10,45,51–52` supplies the live defaults. `view.qc:1039` calls `HUD_Crosshair`; `crosshair.qc:213–256,629–643` gates it and resets transitions. | `NetGame.UpdateHudMirror` and `UpdateFullHudPlayer` feed the same `CrosshairPanel` on a listen host and an ENet client. `UpdateCrosshairGate` reads the live scoreboard and view state. `NormalCrosshairEnabled` checks the four cvars. |
| Art and color | `crosshair.qc:331–349` uses the switching weapon's art and size by default. The numbered preference is 16. | `ResolveCrosshair`, `PerWeaponSizeMult` and `BaseColor` resolve weapon metadata, real VFS art and the selected color mode. The optional number dictionary has no bearing on stock weapon art resolution. |
| Chase | `crosshair.qc:259–301` traces from the viewed eye with `MOVE_WORLDONLY` and projects the impact. Separately, the camera ray tests whether the viewed body obstructs the aim. Alpha moves at 5 units per second toward the bounded preference (0.25 by default) or back to the body's normal alpha. | `UpdateCrosshairGate` feeds `CrosshairTrace` from the body eye and rendered camera. `ChaseBodyObstructs` tests the body hull and nearer collision. `SetChaseScreenPoint` invalidates the canvas when the projection changes. `ClientWorld.DriveChaseBodyAlpha` applies absolute alpha without brightening a body with lower network alpha. |
| Remote Vortex ring | `common/wepent.qc:25–27,61–63` sends the charge and pool. `crosshair.qc:476–521,578–582` draws the clipped outer and optional inner ring. | Server `WepentResolver` → `ServerNet.ResolveOwnerWeaponRings` → `OwnerWeaponRings.Write` → `ClientNet.HandleSnapshot` → `NetGame.UpdateCrosshairWeaponRings` → `CrosshairPanel.DrawStatRing`. Following another player has a distinct `WepentView` feed. |

The old host-only reticle, dead weapon rings, missing per-weapon art, missing rainbow and missing RGB easing descriptions are stale. Source confirmed those paths independently of the graphical checks. The actual issues here were zero alpha being replaced with the fallback opacity, number zero falling through to a drawable style, missing view-lock suppression, unconditional chase fade using inverted alpha, and the procedural Vortex ring bypassing its cvar. The remote facade clock kept the initial reticle and ring in their fade-in state. The panel now reads the advancing render clock. Classic chase and view lock read client cvars on threaded listen hosts.

ADR-0001 keeps native presentation idiomatic. ADR-0002 selects Forward+ for native rendering. ADR-0005 keeps the native owner snapshot format, and ADR-0018 retains stock cvars and defaults without a config fork. The fixture compares original QC with the native HUD path. ADR-0019's legacy VM runs a different client stack.

The native `crosshair_2d` preference remains unconsumed by the separate free-aim cursor. The chase obstruction test uses the ordinary native trace service and lacks the original warpzone traversal. Objective rings, the other weapon rings, spectator chase, and vehicle reticles were outside the graphical trial. [Vehicle evidence](../vehicle-reticles/README.md)

## Reproduce

From the repository root, build the native game with `dotnet build VortexArena.csproj --no-restore`. Run sequentially under Xvfb with `xdotool` and ImageMagick available. Each output path must be new. A cold software-rendered native load can take several minutes.

```sh
script=planning/parity/crosshair/reproduce.sh
xvfb-run -a -s '-screen 0 1000x750x24' "$script" stock /tmp/crosshair-stock
PARITY_PORT=38251 xvfb-run -a -s '-screen 0 1000x750x24' "$script" native /tmp/crosshair-native
PARITY_PORT=38261 CAPTURE_PHASE=chase NATIVE_TOPOLOGY=listen GODOT_RENDERER=forward_plus \
  xvfb-run -a -s '-screen 0 1000x750x24' "$script" native /tmp/crosshair-chase
python3 planning/parity/crosshair/measure.py /tmp/crosshair-stock /tmp/crosshair-native
```

The remote run uses GL Compatibility to keep the connection responsive under software rendering. The native visual mesh fade uses Forward+ and was checked on a threaded listen host. All server connections use loopback. Override `PARITY_PORT` with a free port when running alongside other fixtures. The adjacent native query port must also be free. Environment overrides at the top of the script select the QC checkout, stock binary and Godot executable.

## Observed results

The paired remote clients drew the default Vortex reticle, hid it for master off, number zero, alpha zero, size zero, view lock and held scores, and restored it when the gate cleared. Disabling the Vortex ring left only the named Vortex reticle. The before-fix native remote run drew zero centre pixels above threshold at the default settings. The listen host drew the reticle under those settings, which isolated the clock defect to the remote path. [Paired remote crops](remote-gates.webp) · [Pixel counts](measurements.txt)

At `chase_up 24` the elevated camera showed a projected reticle below centre. Toggling `crosshair_chase` returned the reticle to centre while the body stayed opaque. At `chase_up 0` the body obstructed the aim ray and visibly faded. Setting alpha to 1 or turning projection off restored opacity at that pose. Raising the camera again restored the clear view. These are settled frames from the original remote and native Forward+ listen sessions. Compare each renderer against its own control frame because the view directions and lighting differ. [Paired chase captures](chase.webp)

### Followed pose loss and interpolation

Run `python3 planning/parity/crosshair/check-follow.py planning/parity/crosshair/follow-transition.txt` from the repository root to validate the saved trace. The replay uses the same checker on the live viewer log. It checks the ordered transitions, player identities, projected point and applied opacity.

The follow camera and `NetGame.UpdateCrosshairGate` share `ClientNet.TryGetFollowedPose`. It takes the interpolated pose when available and otherwise uses the newest decoded origin and angles. A positive `cl_nolerp` setting bypasses interpolation and the follow-angle smoother. When both poses are unavailable, the camera uses its existing fallback while the reticle drops its projection and the renderer clears the chase alpha target.

The native loopback fixture builds a disposable viewer with a diagnostic that withholds both poses while leaving the graphical model available. It logs interpolation and raw-state availability, camera follow state, the reticle anchor and the alpha applied by `PlayerModel`. Compatibility rendering is used for this diagnostic, so the alpha evidence comes from the applied model state. The earlier Forward+ capture covers visual transparency.


With `cl_nolerp 2`, the fixture reported `sample=False` and `raw=True`. The camera stayed on the followed pose, the reticle stayed at `(400, 296.54562)` and applied body alpha stayed at 0.25. With both pose sources withheld, the camera took its fallback, the visible reticle centered and the body returned to 1.0 alpha. Restoring the raw pose recovered the camera, projection and 0.25 alpha before interpolation was re-enabled. The same replay passed pose loss and recovery in interpolated mode, then verified the `crosshair_chase` toggle and follow release. [Ten verified transitions](follow-transition.txt)

```sh
xvfb-run -a -s '-screen 0 1000x750x24' planning/parity/crosshair/reproduce-follow.sh /tmp/crosshair-follow
```

### Vortex ring material and size

The pinned `common/weapons/weapon/vortex.qh:22–31` selects `gfx/crosshairnex`, RGB `0.459 0.765 0.835` and the `0.65` weapon multiplier. The TGA files at the pin and the native VFS PNGs for both `crosshairnex` and `crosshair_ring_nexgun` decode to identical 128 × 128 RGBA images. With size `0.4`, the named reticle uses a 33.28-pixel quad at the 800 × 600 viewport. Stock `crosshair.qc:585–615` uses normal blending for that quad. The measured stock and native reticle each fit that model to 0.41 RGB levels RMS after accounting for the background, so the reticle draw stayed in place.

Stock `crosshair.qc:476–521,578–582` passes the quad width times `crosshair_ring_size 2` and uses `DRAWFLAG_ADDITIVE`. The helper at `client/draw.qc:183–323` maps the texture square onto a diamond whose diagonals equal that size. A partial charge clips from the right towards the bottom in screen space. The former native path alpha-blended a circular fan across the full diagonal. The weapon-stat pass now uses the stock UV transform and clip on its own additive canvas below the reticle. The HUD path is independent of the Original/Mixed/Modern particle routing in `planning/particles-dual-system.md` §D. No effect authoring or particle routing changes are part of this pass.

At full charge, the isolated ring footprint went from 66 × 66 native pixels before the fix to 46 × 46 after, matching stock at the same threshold. The old ring darkened 33 pixels by more than two RGB levels in any channel. The rebuilt native and stock rings darkened zero. The rebuilt remote client also matched the stock clipped footprint at a configured 62.5% charge. These captures exercise the outer Vortex ring and its named reticle only. [Before and after crops](ring-material.webp) · [Pixel measurements](ring-material.txt)

The fixture's `CAPTURE_PHASE=ring` takes three same-camera frames for the full draw, ring off, and master off. Run it once per engine and charge setting. Leave `VORTEX_CHARGE` unset for full charge, or set it to `.625` on both runs for the clipped case. The example assumes fresh output directories.

```sh
script=planning/parity/crosshair/reproduce.sh
CAPTURE_PHASE=ring xvfb-run -a -s '-screen 0 1000x750x24' "$script" stock /tmp/vortex-ring-stock
CAPTURE_PHASE=ring PARITY_PORT=38371 xvfb-run -a -s '-screen 0 1000x750x24' "$script" native /tmp/vortex-ring-native
python3 planning/parity/crosshair/measure.py --ring /tmp/vortex-ring-stock /tmp/vortex-ring-native
```

For the clipped run pass `--fraction .625` to the measurer. It compares the reticle to its own disabled frame and the ring to the named reticle alone. The reported blend-model errors are RMS on 8-bit RGB levels.

The build succeeded. The focused selection passed 74 codec, weapon state and client feedback tests. These tests cover state and packet behavior. The paired match captures verify the rendered output.

```sh
dotnet test tests/VortexArena.Tests/VortexArena.Tests.csproj --no-restore \
  --filter 'FullyQualifiedName~OwnerWeaponRingsTests|FullyQualifiedName~WepentViewStateTests|FullyQualifiedName~ClientFeedbackTests'
```
