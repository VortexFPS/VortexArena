# Rows 8–10 · Join notices and client state

Source pin `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`. The native implementation keeps the explicit player model and ENet boundary specified by ADR-0007 and ADR-0011. The saved two-recipient, paused-clock and mode-2 reconnect runs remain the prior evidence. [Saved verification](../S1-VERIFICATION.md)

## Notice delivery and expiry

Stock `ClientConnect` calls `sv_notice_join` only for real clients. Nonempty configured text arms `defer(..., 1, ...)`. On delivery `sv_notice_join_think` reads the active cvars, splits on `|` and sends one `MSG_ONE` temp record per line to the new client. `cl_notice_read` sets each deadline at receipt from CSQC `time + ReadLong()`. `cl_notice_run` draws only entries whose deadline is strictly after `time`. The only reference to `sv_notice_toall` beyond its definition is its declaration. The reader stores the modal byte, and the pinned draw loop has no modal branch.

Native retains its one reliable grouped notice per peer. A receipt with an accepted snapshot clock now starts the lifetime immediately, even while the load screen remains. A receipt ahead of the first accepted snapshot starts on the first available snapshot clock. The HUD shows the remaining whole seconds rounded up and drops a notice at its deadline. The two new delayed-load checks used real loopback ENet clients and a temporary hold on loading dismissal. The saved graphical captures supply color and recipient evidence. The new headless traces supply timing and visibility-call evidence. Packet loss, long gaps before the first snapshot and nonzero time scaling remain unmeasured. [Delayed-load traces](evidence.txt)

## Attach, retain and detach

| Stock consumer | Native behavior |
| --- | --- |
| `state.qh` defines owner back-references and a checked `CS()` in debug builds. `Client` initialization attaches once. `m_unwind` preserves the connection when `Observer`, `Spectator` and `Player` transmute. | `ClientManager.ClientInfo`, `Player` and `ServerPlayerStates` keep one connected client through join, spawn and observation. The verified native identity is present on the player before connect hooks apply mute, vote-master and other identity-based state. |
| `PlayerState_attach` creates owned inventory on `PutPlayerInServer` and reuses an already-owned state. `SpectateCopy` and `CopyBody` borrow stock player state. `PutObserverInServer` detaches, clears the observer inventory route and frees owned state after clearing client aliases. | Native rebuilds the live loadout on spawn, clears live weapons and status on observation, copies selected spectator fields and gives body copies their own gameplay fields. The typed objects avoid sharing inventory pointers between clients. |
| `ClientConnect` sets `FL_CLIENT` before transmutation. `PutObserverInServer` uses `FL_CLIENT\|FL_NOTARGET`, strips pickup ability and sets a non-solid spectator hull. A fresh live spawn sets `FL_CLIENT\|FL_PICKUPITEMS`. `ClientDisconnect` clears `FL_CLIENT` after its client-only cleanup. | `EntFlags.Client`, `EntFlags.NoTarget`, `IsObserver` and `CanPickupItems` give the live and observe paths the same gates. `ClientDisconnect` clears connection and pickup flags after removal. The focused test checks the ordinary connect, join, respawn, observe and disconnect transitions. |
| `CS_CVAR` addresses the client edict because values can arrive before the attached object exists. The client starts replication in `PostInit`. `sentcvar` takes the caller as the store and `GetCvars(..., -1)` frees it on detach. | Native accepts `sentcvar` once the peer is admitted, including before the first playing spawn. Mode 2 starts false until updated. The same client's preferences survive respawn and observation. The world now discards that client's table on any normal disconnect. The saved replacement connection started fresh while Alpha's preference remained intact. |
| `ClientState_attach` runs `DecodeLevelParms`, score, player-basic stats, client data, accuracy, entcs, anticheat, hitplot, inventory and status setup. `SetChangeParms` carries a relative idle clock across engine level changes. `DecodeLevelParms` applies the 60-second minimum idle allowance relative to `sv_maxidle`. | `InfraClientConnect` now seeds the original fresh-client idle grace. Input activity resets the clock, while respawn retains it and the kill anti-spam carryover. Native map rehosting uses its own connection lifecycle. Same-channel stock level-parm transfer remains separate. |
| `ClientState_detach` frees cvars, accuracy, scores, client data, entcs, inventory and status, then removes bot state and reports anticheat. `W_HitPlotOpen` and `W_HitPlotClose` bind the opt-in file handle to the connection. | Roster and snapshot removal retire score, accuracy and client visibility state. The world clears preferences, timer and status ownership, anticheat and bot state. `HitPlots` opens one local file per opted-in connection, closes it on detach and closes open writers at map shutdown. The file tests and previously saved local hitplot runs cover these effects. |

The new lifecycle tests establish local identity-based mute and vote-master application, the fresh idle grace, same-connection retention and replacement-connection cleanup. External playerstats transactions and custom modal notice producers are outside the evidence. [Focused verification](evidence.txt)

## Reproduce the notice traces

From a restored Linux checkout with `.godot` populated and the Stormkeep and shared asset packs installed, pass the Godot 4.6.3 Mono executable to the Python 3.9+ driver. It copies the project into a fresh output tree, instruments and builds that copy, and runs a dedicated server and two headless ENet clients on loopback with separate profiles. The game data is mounted from the checkout. The driver checks receipt, the 1.5- or 4-second hold, countdown remaining on release, and expiry at the original deadline. It prints the checked events and retains the complete logs in the output tree.

```sh
python3 planning/parity/unmapped-audit/client-state-notices/reproduce.py --godot "$GODOT_MONO"
```

The stock comparison uses the same delay lengths in CSQC `time`. The disposable CSQC program leaves stock receipt and expiry active and holds only the notice draw call. This models a viewer waiting after receipt. It does not simulate DarkPlaces map signon or establish when receipt first becomes possible during an actual stock load. The driver uses Xvfb and checks the released SDL binary, pinned server program and preprocessed pinned client source before compiling the instrumentation with the prepared `gmqcc`. It also checks both stock lines get the same receipt deadline. [Reference setup](../../review/superweapons-poweroff/REPRODUCE.md)

```sh
python3 planning/parity/unmapped-audit/client-state-notices/reproduce.py \
  --engine both --godot "$GODOT_MONO" \
  --base /tmp/vortex-parity-base --stock /tmp/va-reviewed-stock
```

The independent repeat checked the first observed client frame at or beyond each deadline. Native hid the visible notice at 14.640625 and discarded the hidden one at 29.750000. Stock hid at 14.420598 against a 14.398864 deadline and discarded at 28.110287 against 28.078232. Each action occurred on the first eligible frame. The verifier counts the seconds actually remaining when a delayed frame can draw. [Measured client clocks](evidence.txt)

The [four clock traces](clock-traces/) retain every sampled frame and notice marker from the independent repeat. From the repository root, run `python3 planning/parity/unmapped-audit/client-state-notices/reproduce.py --check-saved planning/parity/unmapped-audit/client-state-notices/clock-traces`. The check requires presentation on the first eligible sample after release and verifies that both stock lines arrived in the same frame.
