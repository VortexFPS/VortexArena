# Perf baselines

Known-good `tools/perf-report.py --json` summaries, one per (map, build-flavor), used by
`tools/perf-run.ps1 -Baseline …` and `tools/perf-smoke.ps1 -Live` to answer "is this run worse
than known-good?".

Since 2026-07-06 the capture convention is perf-run's **demo scenario** (spectate a bot
first-person, all 8 core weapons rotating, forced respawn), 90 s, 6 bots, the pinned capture
profile (see perf-run.ps1) — real traversal + gunplay, not the old stand-at-spawn idle camera
(whose steady state measured almost nothing: it never moved, so e.g. the true-aim trace cache
never missed). Diff post-load (`pl`) rows for steady-state claims; full-session rows include
load/join.

Regenerate on a known-good **release export**:

```powershell
tools\perf-run.ps1 -Label baseline -Map catharsis -Secs 90
Copy-Item _scratch\perf_baseline.json tools\perf-baselines\catharsis-release.json
```

Naming: `<map>-release.json`. Commit updates deliberately (a baseline change is a claim that the
new numbers are the accepted normal), and note the reason in the commit message.
