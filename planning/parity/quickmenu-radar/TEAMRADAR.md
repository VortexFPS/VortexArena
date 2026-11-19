# S1 row 2 — teamradar geometry and pulse history

Reference `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`. The relevant pin paths are `client/teamradar.qh`, `client/teamradar.qc`, `client/hud/panel/radar.qc`, `common/util.qc` and the waypoint send/receive handlers in `common/mutators/mutator/waypoints/waypointsprites.qc`. The [existing normal-entry checks](README.md) cover opening, closing, binds and successful Onslaught relocation.

The pin squares and pads the minimap projection by 1/64 on each side, then draws a UV crop bounded by the arena rectangle. The padded square's span cancels out of the world-to-screen scale. It still determines the texture crop and the size of an expanding pulse. Native had drawn the entire square, ignored the authored `mapinfo size` and sized pulses from the fixed 8-pixel icon. Native now reads the mounted bounds for listen and remote clients, uses the cropped UVs, and sizes additive pulses from the fitted or zoomed texture span. Native uses the pin’s screen blend for minimap art and keeps the HUD skin backdrop and existing frame. An unannotated custom `_radar` image uses world-only collision bounds.

Each active ping in native waypoint packets carries its event sequence. The decoder stamps each new event into a 32-slot ring and drops the history with the waypoint or session. It keeps the 0.3-second server cooldown and one-second visual lifetime. The waypoint wire change introduced in protocol 30 adds a per-peer snapshot sequence and sends the final empty list reliably over the existing [authoritative ENet transport](../../decisions/ADR-0005-custom-netcode.md). The native server remains authoritative over Onslaught destinations and uses the existing client-command channel.

## Reproduce

Run from the repo root with the local engine, preprocessed pinned QC and Godot fixtures in the default `/tmp` locations. The harness uses disposable profiles, a stock dedicated server and a native ENet listen server on loopback. It builds disposable instrumented copies for staging two fixed waypoints, pulsing one through the normal server API and recording the real packet decode, HUD draw and server click parse. Native setup commands go through the game's console dispatcher and the zoom bind and click use X11 input. Native fixture startup skips optional asset warmups.

```sh
out=$(mktemp -d /tmp/teamradar.XXXXXX)
fixture=planning/parity/quickmenu-radar
xvfb-run -a -s '-screen 0 900x560x24' "$fixture/reproduce-teamradar.sh" stock "$out/stock"
xvfb-run -a -s '-screen 0 900x560x24' "$fixture/reproduce-teamradar.sh" native "$out/native"
python3 "$fixture/check-teamradar.py" "$out/stock" "$out/native"
```

The Running Man CTF fixture declares radar bounds `(-2400, -1000)` to `(1200, 1800)` and stages the player facing 37°. Both clients use the same 720 × 420 overview, waypoints at `(-300, 100, 64)` and `(500, 900, 200)`, and a mirrored picker click at viewport pixel `(527, 321)`. The verifier compares projected pixels and the server-parsed fractional coordinates. It also checks the 36-ping sequence across slot rollover, overlap and expiry.

## Results

Both engines recorded all 36 sends, reached three simultaneous pulses at the same waypoint, wrapped from slot 31 to 0 and returned to zero visible pulses after expiry. The paired trace measures the north fit, live zoom endpoints, intermediate zoom fractions and the mirrored view. Across the two waypoints, the largest measured endpoint difference was 0.02 pixels. [Verifier output](teamradar-check.txt) · [Raw wrap and transform samples](teamradar-trace.txt)

The stock server parsed `(-1501.708496, 819.787598, 95.000000)` and the native server parsed `(-1501.694092, 819.818481, 95.000000)`. Both picks are within 0.04 world units of the analytic inverse for the scripted pixel. The native server uses the camera's Z and sees six decimal places on every axis.

The paired radar crop shows the stock frame on the left and the native frame on the right. Pulse ages differ because the screenshots were taken after the three-active-pulse trace in each engine. [Pulse frames](teamradar-pulses.webp)

The Godot project build and 199 focused Onslaught, waypoint, map-info, snapshot and HUD-editor tests passed.


## Screen blend, custom texture bounds and last-waypoint delivery

The pin’s `DRAWFLAG_SCREEN` uses `dst + src × (1 − dst)`. Radar foreground alpha shades the source RGB. [The HUD parity contract](../../HUD_PARITY_CONTRACT.md) specifies the foreground settings and skin frame. The native minimap draws over that backdrop using a radar-sized screen copy. The empty-art fallback keeps its existing fill.

The unannotated custom `_radar` fixture adds 64 units to each BSP XY bound and leaves world collision intact. Both clients swept the full original cross-section from both sides and returned `(-2944.031, -1280.031)` to `(1536.031, 3200.031)`. The authored `mapinfo size` path retains priority. Both engines left black texels transparent and brightened grey texels according to the screen equation. The stock and native scenes use their own skin and rendering settings. [Blend frames, stock left and native right](teamradar-blend.webp)

The native fixture delivered one visible waypoint, suppressed three subsequent best-effort snapshots, killed the marker and sent one reliable empty list. It replayed the saved old datagram only after the real decoder applied the clear. The receiver stayed empty and then accepted a newer visible waypoint. All four messages carried simulation time 16. The test fixes the wire time while the engine runs to cover equal-time ordering deterministically. An engine pause with no simulation ticks still defers normal snapshot broadcasting until the next tick. [Verifier output](teamradar-followup-check.txt) · [Raw bounds and packet trace](teamradar-followup-trace.txt)

Run the follow-up with the same local fixtures.

```sh
out=$(mktemp -d /tmp/teamradar-followup.XXXXXX)
fixture=planning/parity/quickmenu-radar
RADAR_VARIANT=followup xvfb-run -a -s '-screen 0 900x560x24' "$fixture/reproduce-teamradar.sh" stock "$out/stock"
RADAR_VARIANT=followup xvfb-run -a -s '-screen 0 900x560x24' "$fixture/reproduce-teamradar.sh" native "$out/native"
python3 "$fixture/check-teamradar.py" --followup "$out/stock" "$out/native"
```

The project built and all 200 focused tests passed after the follow-up.

## Check the saved runs offline

The [archived inputs](teamradar-inputs.zip) contain every radar diagnostic line from the two comparisons and the four original PNGs used for the blend measurements. The manifest records the SHA-256 of each full input file and of its archived bytes. Only lines beginning with `RADAR_` are included from the logs. The screenshots are complete. The archive uses the last stock run with the console-key binding for the blend comparison.

```sh
set -eu
fixture=planning/parity/quickmenu-radar
out=$(mktemp -d)
unzip -q "$fixture/teamradar-inputs.zip" -d "$out"
python3 "$fixture/check-teamradar.py" "$out/stock" "$out/native"
python3 "$fixture/check-teamradar.py" --followup "$out/stock" "$out/native"
```

The loss trial covers the transition to zero waypoints and a subsequent new marker. Delivery of every pulse when several occur between accepted snapshots remains unmeasured. The two clients sampled their zoom animations on independent clocks.

## Onslaught link removal

The pin links each `onslaught_link` as a separate `ENT_CLIENT_RADARLINK`. The stock fixture names the six link entities and removes them with the existing `killtarget` command. CSQC's live radar draw goes from six lines to zero in the same session.

Native builds the display graph from live endpoint entities. The fixture gives two native pads their map names so the existing `killtarget` command can remove them. Every edge uses one of these two pads. The server continues through `CollectRadarLinks`, the normal packet encoder and ENet. A separate graphical client joins the dedicated server through a loopback UDP relay. The relay drops the first datagram containing the reliable empty graph and passes its retransmission. The fixture then sends a previously captured nonempty link snapshot over the normal best-effort channel. The test waits for the client decoder to reject that exact sequence with its graph still empty.

Native wire version 32 adds a shared link sequence to both channels, following version 31's inline-brush change. Moving or recolored links still use best-effort snapshots. The fixture extends only its own ENet timeouts to accommodate cold software rendering.

```sh
out=$(mktemp -d /tmp/teamradar-links.XXXXXX)
fixture=planning/parity/quickmenu-radar
RADAR_VARIANT=links xvfb-run -a -s '-screen 0 900x560x24' "$fixture/reproduce-teamradar.sh" stock "$out/stock"
RADAR_VARIANT=links xvfb-run -a -s '-screen 0 900x560x24' "$fixture/reproduce-teamradar.sh" native "$out/native"
cat "$out/stock/links.log" "$out/native/relay.log" "$out/native/links.log"
```


The joined native client drew six lines, applied clear sequence 5603 after the relay recorded the retransmission, and drew zero lines. Replay sequence 1566 was rejected and the graph stayed empty. The build and 200 focused tests passed. [Raw link trace](teamradar-links-trace.txt) · [HUD frames, stock left and native right](teamradar-links.webp)
