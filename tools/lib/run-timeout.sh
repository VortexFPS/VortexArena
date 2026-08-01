# shellcheck shell=sh
# ---------------------------------------------------------------------------------------------------
# run_with_timeout SECONDS COMMAND [ARGS...] — a portable `timeout`.
#
#     . "$ROOT/tools/lib/run-timeout.sh"
#     run_with_timeout 180 "$GODOT" --headless --path "$ROOT" > "$log" 2>&1 || true
#
# WHY. GNU coreutils' `timeout` is not part of BSD userland, so it does not exist on macOS unless someone
# has run `brew install coreutils` (which installs it as `gtimeout`). ci/ci.sh used it to bound three
# headless Godot runs and tools/perf-run.sh to bound a capture; on a Mac all four died with
# `timeout: command not found`. Note this is not a purely theoretical portability worry — ci/ci.sh already
# carried a comment that Windows `timeout` cannot kill the Godot child, so the assumption that one `timeout`
# behaves the same everywhere was already known to be shaky.
#
# STRATEGY: prefer the real tool wherever it exists, so Linux, CI and Git Bash behave EXACTLY as before and
# this file changes nothing for them. Only a machine with neither `timeout` nor `gtimeout` takes the shell
# fallback, which is macOS-without-coreutils and little else.
#
# POSIX sh ONLY — see the note in find-godot.sh.
# ---------------------------------------------------------------------------------------------------

# Resolved once at source time.
if command -v timeout >/dev/null 2>&1; then
    _RT_CMD=timeout
elif command -v gtimeout >/dev/null 2>&1; then
    _RT_CMD=gtimeout          # coreutils on macOS/BSD installs the GNU tools g-prefixed
else
    _RT_CMD=""
fi

# Report which implementation is in use (diagnostics; `vx doctor` will want this).
timeout_impl() { [ -n "$_RT_CMD" ] && printf '%s\n' "$_RT_CMD" || printf 'shell-fallback\n'; }

run_with_timeout() {
    _rt_secs="$1"; shift
    if [ -n "$_RT_CMD" ]; then
        "$_RT_CMD" "$_rt_secs" "$@"
        return $?
    fi

    # ---- fallback: supervise it ourselves -----------------------------------------------------------
    # STDIN MUST BE FORWARDED EXPLICITLY. POSIX requires an asynchronous list's stdin to be assigned to
    # /dev/null before any explicit redirection, so a bare `"$@" &` silently gives the child an empty
    # stdin — which breaks ci/ci.sh's dedicated smoke, whose whole point is piping console commands in
    # (`{ sleep 12; echo status; echo quit; } | run_with_timeout 240 "$GODOT" ...`). It fails as "the
    # console never responded", nowhere near the cause. Duplicating stdin onto fd 3 first and redirecting
    # the child from that is unambiguous, where `<&0` would race the /dev/null assignment.
    exec 3<&0
    "$@" <&3 &
    _rt_pid=$!
    exec 3<&-

    # Watchdog. Polls at 1 s rather than sleeping the whole duration so it exits promptly when the child
    # finishes on its own — a single long `sleep` would otherwise linger for the full timeout after every
    # successful run, and `kill`ing the subshell does not reliably reap a sleep it is blocked in.
    (
        _rt_i=0
        while [ "$_rt_i" -lt "$_rt_secs" ]; do
            kill -0 "$_rt_pid" 2>/dev/null || exit 0    # finished on its own; nothing to do
            sleep 1
            _rt_i=$((_rt_i + 1))
        done
        # TERM first so the process can flush (Godot writes a session log on exit), KILL if it will not go.
        kill -TERM "$_rt_pid" 2>/dev/null
        sleep 2
        kill -KILL "$_rt_pid" 2>/dev/null
    ) &
    _rt_watch=$!

    # `|| _rt_rc=$?` rather than a bare `wait`, because every caller runs under `set -e` and a non-zero
    # child would otherwise abort the script before the status could be returned.
    _rt_rc=0
    wait "$_rt_pid" 2>/dev/null || _rt_rc=$?
    kill -TERM "$_rt_watch" 2>/dev/null
    wait "$_rt_watch" 2>/dev/null || true

    # NOTE: GNU timeout reports 124 when it fires; this path returns the signal-derived status instead
    # (143 for TERM, 137 for KILL). Every call site in this repo appends `|| true` and judges success by
    # inspecting the log, so the distinction has no consumer today — but do not add one without making
    # this return 124, or the two implementations will disagree on the one thing that matters.
    return "$_rt_rc"
}
