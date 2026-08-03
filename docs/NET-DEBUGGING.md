# Net / movement debugging — `net_input_trace`

A dormant, built-in diagnostic for the **client → transport → server → movement → prediction** pipeline. It is
how the catharsis "first ~5 s rubberbands me to spawn" bug was finally pinned to ENet's unreliable-packet throttle
after several wrong guesses — so reach for it *first* the next time movement or networking feels off, instead of
theorising from the code.

## Using it

In the in-game console:

```
set net_input_trace 1     # start logging
# ... reproduce the issue (join a match, move around, etc.) ...
set net_input_trace 0     # stop
```

A `[nettrace]` line is printed every ~0.25 s to the console / log (the windowed build's stdout goes to the
`_console` Godot binary; a `--quit-after-seconds` headless run captures it too). It is **off by default** and costs
nothing when off — the counters it reads are single increments on the hot path, left always-on so the numbers read
true the instant you enable the trace.

## The line

```
[nettrace] dt=16ms push=418 send=418 recv=2128 enq=362 batch=362 | enet throttle=32/32 loss=0 rtt=30ms | pred=<…> srvOrg=<…> recon=0.00
```

| field | meaning | source |
|---|---|---|
| `dt` | this render frame's wall-clock delta (ms); spikes = hitches | NetGame |
| `push` | input commands the client **generated** (cumulative) | ClientNet |
| `send` | input datagrams the client **transmitted** (cumulative) | ClientNet |
| `recv` | input command-copies the server **received off the wire** (incl. the redundant tail) | ServerNet |
| `enq` | commands that passed the **seq-dedup** into a per-peer queue (unique) | ServerNet |
| `batch` | commands **drained into a movement batch** and applied | ServerNet |
| `enet throttle` | ENet per-peer **unreliable** packet throttle `value/limit` (0..32); low = drops input | ENet |
| `loss` | ENet measured packet loss (0..65536 ≈ 0..100 %) | ENet |
| `rtt` | ENet smoothed round-trip estimate (ms); ~0 on loopback once converged | ENet |
| `pred` | the client's **predicted** local origin (what the camera follows), Quake space | Reconciler |
| `srvOrg` | the **authoritative** server origin for the local player (listen server only) | GameWorld |
| `recon` | last reconcile **error** magnitude (qu) | Reconciler |

## Reading it — failure signatures

Walk the pipeline left to right; the column where the numbers stop tracking is where the problem is.

- **`push` ≈ `send`, both ~1/frame** → the client is generating + transmitting input fine. (If `send` ≪ `push`,
  the transmit is gated — check `cl_netfps` / `InputSendInterval`.)
- **`recv` grows far slower than `send`** → datagrams are being **dropped in transport**. Check `enet throttle`
  (a low value drops unreliable sends — the spawn-stutter cause) and `loss`. *Fixed by* the per-peer
  `ThrottleConfigure` in `NetTransport.HookSignals`.
- **`recv` ≫ `enq`** → lots of duplicates/redundancy (expected: each datagram carries ~3 commands), or a seq
  problem if the gap is extreme.
- **`enq` ≫ `batch`** → the server is **not draining its input queue** (a tick-budget / consume-rate problem).
- **`send` − `enq` is large and constant** → a steady **delivery backlog** (server processes input N commands
  behind). Usually benign for *feel* if `recon≈0` (command-driven prediction covers it — the player sees the
  correct predicted position; the server just confirms it late), but it deepens the reconcile window.
- **`pred` runs ahead of `srvOrg` then snaps back, `recon` spikes periodically** → the predictor is moving while
  the server is **starved or frozen** (input not arriving, or a pre-match `canMove=false`). This is the visible
  rubberband. Trace upstream: is it `recv`/`enq` collapsing (transport/queue), or is the server intentionally
  holding the player (countdown freeze, `gs`/`MatchStartTime`)?
- **`recon` steady-nonzero while moving normally** → a genuine predict-vs-authority **desync** (the
  `PREDICTION DESYNC` Prof event also fires); the sims are computing different results.

## Worked example — the spawn-stutter

Symptom: the first ~5 s on catharsis, walking forward rubberbanded the player back toward spawn. The trace showed
`push`/`send` climbing 1/frame but `recv` crawling +3 every ~32 frames, with `enet throttle=0/32`, `loss=0` — then
at ~5 s `throttle` jumped `0→32` and `recv` instantly caught up. I.e. ENet's unreliable throttle starts pinned near
0 and only recovers at its 5 s recalc interval (`ENET_PEER_PACKET_THROTTLE_INTERVAL`), silently dropping ~94 % of
input on a fresh connection. Fix: reconfigure each peer's throttle on connect to recover fast
(`NetTransport.HookSignals` → `ThrottleConfigure(100 ms, 32, 4)`).

## Related diagnostics

- `cl_frameprofiler 1` — frame-time + GC hitch monitor (the `proc/rcpu/gpu/rest` breakdown; add
  `cl_frameprofiler_rendertime 1` for the `rcpu`/`gpu` half, which is off by default — see PERF-DEBUGGING.md).
- `developer 1`/`2` — engine log verbosity (the `[reconcile] origin SNAP …` and `PREDICTION DESYNC` traces).
- `--camera-trace <scenario> <out>` — headless deterministic capture of predicted/view origin + reconcile error
  (see `tools/camera-ref/`).
