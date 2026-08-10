#!/usr/bin/env bash
# Bring up N env hosts on this machine for a trainer running elsewhere.
#
#     tools/neural/worker.sh --bind 10.0.10.61 --port 5001 --count 24 [-- --no-warps ...]
#
# Then, on the trainer machine:
#
#     python tools/neural/train.py --hosts 0 --remote 10.0.10.61:5001:24 ...
#
# WHAT CROSSES THE NETWORK is simulation, not gradients. One trainer keeps the policy, the optimiser and the
# batch; each host runs the game and returns observations. At 16 agents a frame is about 19 KB and a step is
# one round trip, so a LAN carries this easily. A WAN will not -- the round trip is synchronous and happens
# every rollout step, so latency lands directly on throughput.
#
# BIND ADDRESS IS A SECURITY DECISION. The protocol has no authentication: whoever connects can drive the
# simulation and read observations back. Bind to a specific LAN address on a network you trust, never to
# 0.0.0.0 on anything reachable from outside. The host defaults to loopback and only widens when told.
#
# HOW MANY HOSTS. Each host is roughly one core and 300-450 MB. For a machine that should stay usable while
# it contributes, half the core count is a reasonable cap; for a dedicated box, cores minus two leaves room
# for the OS and the trainer's own eval shards if they run here.
set -euo pipefail

BIND=""; PORT=5001; COUNT=0; DLL=""
HOST_ARGS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --bind)  BIND="$2"; shift 2 ;;
    --port)  PORT="$2"; shift 2 ;;
    --count) COUNT="$2"; shift 2 ;;
    --dll)   DLL="$2"; shift 2 ;;
    --)      shift; HOST_ARGS=("$@"); break ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

[[ -n "$BIND" ]] || { echo "--bind is required (an IP on this machine; see the note above)" >&2; exit 2; }
if [[ "$COUNT" -le 0 ]]; then
  CORES=$(nproc 2>/dev/null || echo 4)
  COUNT=$(( CORES > 3 ? CORES - 2 : 1 ))
  echo "[worker] --count not given, using $COUNT (cores minus two)"
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
[[ -n "$DLL" ]] || DLL="$ROOT/tools/neural/VortexArena.NeuralHost/bin/Release/net8.0/va-neural-host.dll"
[[ -f "$DLL" ]] || { echo "host not built: $DLL" >&2; exit 1; }

LOGS="$ROOT/_scratch/worker"; mkdir -p "$LOGS"
PIDS=()
cleanup() {
  echo "[worker] stopping ${#PIDS[@]} host loops"
  for p in "${PIDS[@]:-}"; do kill "$p" 2>/dev/null || true; done
  # The loops are dead but their current dotnet children are not; those PIDs were written per slot.
  for f in "$LOGS"/host_*.pid; do
    [ -f "$f" ] && kill "$(cat "$f")" 2>/dev/null || true
  done
}
trap cleanup EXIT INT TERM

echo "[worker] $COUNT hosts on $BIND:$PORT-$(( PORT + COUNT - 1 ))"
for (( i = 0; i < COUNT; i++ )); do
  P=$(( PORT + i ))
  # Each slot is a respawn loop, not a one-shot host. A host exits by design when its trainer connection
  # closes -- one connection per process -- which used to mean every trainer restart needed someone to ssh
  # in and relaunch the whole fleet. Now the slot notices, waits a beat, and listens again.
  #
  # stdout to a file, not to a pipe nobody drains. The game writes a line per map load, and a full 64 KB
  # pipe buffer blocks the host forever mid-write -- a failure that reads as a hard crash at a suspiciously
  # reproducible step number. The log is truncated per respawn so it describes the CURRENT host, and the
  # dotnet PID is written per slot so cleanup can reach the child the loop cannot.
  (
    while true; do
      dotnet "$DLL" --port "$P" --bind "$BIND" --data "$ROOT/data" "${HOST_ARGS[@]:-}" \
          > "$LOGS/host_$P.log" 2>&1 &
      CHILD=$!
      echo "$CHILD" > "$LOGS/host_$P.pid"
      wait "$CHILD"
      sleep 1
    done
  ) &
  PIDS+=("$!")
done

echo "[worker] up. trainer: --hosts 0 --remote $BIND:$PORT:$COUNT"
echo "[worker] Ctrl-C to stop"
wait
