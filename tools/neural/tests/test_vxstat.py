"""Regression tests for vxstat's live terminal layout and automatic fleet discovery."""

import importlib.machinery
import importlib.util
import os
from pathlib import Path


VXSTAT_PATH = Path(__file__).resolve().parents[1] / "vxstat"
LOADER = importlib.machinery.SourceFileLoader("vxstat_module", str(VXSTAT_PATH))
SPEC = importlib.util.spec_from_loader(LOADER.name, LOADER)
vxstat = importlib.util.module_from_spec(SPEC)
LOADER.exec_module(vxstat)


def test_clip_visible_preserves_ansi_but_never_wraps():
    line = "\033[31mabcdefghij\033[0m"
    clipped = vxstat.clip_visible(line, 5)
    assert vxstat.visible_len(clipped) == 5
    assert clipped.startswith("\033[31mabcde")
    assert clipped.endswith("\033[0m")


def test_live_frame_clears_each_row_and_fully_clears_on_resize(monkeypatch):
    monkeypatch.setattr(vxstat.shutil, "get_terminal_size", lambda fallback: os.terminal_size((40, 8)))
    first, size = vxstat.live_frame(["x" * 100])
    same, size = vxstat.live_frame(["short", "second"], size)

    monkeypatch.setattr(vxstat.shutil, "get_terminal_size", lambda fallback: os.terminal_size((55, 9)))
    resized, _ = vxstat.live_frame(["short"], size)

    assert first.startswith("\033[2J\033[H")
    assert same.startswith("\033[H") and not same.startswith("\033[2J")
    assert same.startswith("\033[H\033[2K\rshort")
    assert "\n\033[2K\rsecond" in same
    assert resized.startswith("\033[2J\033[H")


def test_peer_targets_discovers_workers_and_explicit_account_wins():
    local = {"runs": [{"remotes": [{"addr": "10.0.10.61", "count": 56}]}]}
    assert vxstat.peer_targets(local) == ["vortex@10.0.10.61"]
    assert vxstat.peer_targets(local, ["operator@10.0.10.61"]) == ["operator@10.0.10.61"]


def test_job_block_reserves_five_event_rows_when_empty():
    run = {
        "name": "v27", "running": True, "up": 60, "stage": 3, "phase": "training",
        "stopped_reason": None, "best": 61.0, "gate": 65.0, "baseline": None, "last": [],
        "best_time": None, "steps": 1, "budget": 10, "sps": 1.0, "update": 1,
        "sampled": 100.0, "entropy": 0.5, "kl": 0.01, "skipped": 0, "diverge": 0,
        "starved": 0, "relaxed": 0, "eval_running": False, "eval_secs": 0,
        "eval_elapsed": 0, "eval_done": 0, "eval_total": 4, "eval_every": 60,
        "spu": 1.0, "remotes": [], "agents_per_host": 16, "events": [],
    }
    snap = {
        "machine": "SKYTECH", "hosts": 0, "ram_used": 10, "ram_total": 32,
        "ram_free": 22, "cpu": 0.25, "eval_shards_running": 0, "age": 0,
    }
    lines = vxstat.job_block(run, snap, [], vxstat.Style(False), set())
    state_index = next(i for i, line in enumerate(lines) if "state" in line)
    event_index = next(i for i, line in enumerate(lines) if "events" in line)
    assert "healthy" not in lines[state_index]
    assert "healthy" in lines[state_index + 1]
    assert len(lines[event_index:event_index + vxstat.MIN_EVENT_ROWS]) == 5
    assert all("events" not in line for line in lines[event_index + 1:event_index + 5])

    stopped = dict(run, running=False, phase="stopped")
    stopped_lines = vxstat.job_block(stopped, snap, [], vxstat.Style(False), set())
    fleet_index = next(i for i, line in enumerate(stopped_lines) if "fleet" in line)
    paused_index = next(i for i, line in enumerate(stopped_lines) if "paused - resumes" in line)
    assert paused_index == fleet_index + 1


def test_eval_word_shows_running_eta_and_structured_next_eval():
    style = vxstat.Style(False)
    running = {"running": True, "eval_running": True, "eval_secs": 1800,
               "eval_elapsed": 600, "eval_done": 1, "eval_total": 4,
               "eval_every": 60, "update": 100, "spu": 2.0,
               "next_eval_update": 160}
    assert "~20m 00s left" in vxstat.eval_word(running, style)

    waiting = dict(running, eval_running=False, eval_elapsed=0)
    assert "next eval in 60u" in vxstat.eval_word(waiting, style)


def test_background_eval_keeps_remote_worker_status_on_rollout():
    run = {
        "name": "v27", "running": True, "up": 60, "stage": 3,
        "phase": "training_with_eval", "stopped_reason": None, "best": 61.0,
        "gate": 65.0, "baseline": None, "last": [], "best_time": None,
        "steps": 1, "budget": 10, "sps": 1.0, "update": 1, "sampled": 1.0,
        "entropy": 0.5, "kl": 0.01, "skipped": 0, "diverge": 0, "starved": 0,
        "relaxed": 0, "eval_running": True, "eval_secs": 100, "eval_elapsed": 10,
        "eval_done": 0, "eval_total": 4, "eval_every": 60, "spu": 1.0,
        "next_eval_update": 0, "remotes": [{"addr": "10.0.10.61", "count": 56}],
        "agents_per_host": 16, "events": [],
    }
    local = {"machine": "SKYTECH", "hosts": 0, "ram_used": 10, "ram_total": 32,
             "ram_free": 22, "cpu": 0.5, "eval_shards_running": 4, "age": 0}
    peer = {"machine": "vortex-train", "target": "vortex@10.0.10.61", "hosts": 56,
            "ram_used": 5, "ram_total": 20, "ram_free": 15, "cpu": 0.5, "age": 0}
    lines = vxstat.job_block(run, local, [peer], vxstat.Style(False), set())
    worker_line = next(line for line in lines if "vortex-train" in line)
    assert "simulation rollout" in worker_line
    assert "waiting on eval" not in worker_line
