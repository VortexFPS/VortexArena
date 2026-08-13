"""How the trainer finds the C# environment host, and why it no longer counts parent directories.

Resolution used to be ``Path(__file__).resolve().parents[3]`` — three levels up is the VortexArena root.
That is not a lookup, it is an assumption about where this file lives, and its failure mode is the bad one:
it does not raise when the assumption stops holding, it returns a confidently wrong path. Moving the
trainer into its own repository (planning/neural-bot-lab-migration.md, step 4) breaks it silently.

The order is now: explicit argument, ``VX_NEURAL_HOST``, a ``neural-host.json``, and only then a checkout
identified by a marker file. These tests pin that order and, more importantly, pin the property that makes
the extraction safe: nothing here depends on this file's depth.

Run:  python -m pytest tools/neural/tests -q
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest

NEURAL = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(NEURAL))

from va_neural import env as env_module  # noqa: E402


@pytest.fixture(autouse=True)
def _isolate(monkeypatch, tmp_path):
    """No inherited environment variable, and a working directory with no config in it."""
    monkeypatch.delenv(env_module.HOST_ENV_VAR, raising=False)
    monkeypatch.chdir(tmp_path)


def _write_config(directory: Path, host_dll: str) -> Path:
    path = directory / env_module.HOST_CONFIG_NAME
    path.write_text(json.dumps({"host_dll": host_dll}), encoding="utf-8")
    return path


# --- priority order ------------------------------------------------------------------------------------

def test_environment_variable_wins_over_everything_else(monkeypatch, tmp_path):
    _write_config(tmp_path, str(tmp_path / "from-config.dll"))
    monkeypatch.setenv(env_module.HOST_ENV_VAR, str(tmp_path / "from-env.dll"))
    assert env_module._default_host_binary() == tmp_path / "from-env.dll"


def test_config_is_used_when_no_environment_variable_is_set(tmp_path):
    _write_config(tmp_path, str(tmp_path / "pinned.dll"))
    assert env_module._default_host_binary() == tmp_path / "pinned.dll"


def test_enclosing_checkout_is_the_last_resort(monkeypatch):
    """With no environment variable and no config, the repository this file sits in still works."""
    sentinel = Path("/somewhere/va-neural-host.dll")
    monkeypatch.setattr(env_module, "_host_from_enclosing_checkout", lambda start=None: sentinel)
    assert env_module._default_host_binary() == sentinel


# --- the config file -----------------------------------------------------------------------------------

def test_relative_config_path_resolves_against_the_config_not_the_cwd(tmp_path, monkeypatch):
    """A pinned build beside its config keeps working whatever directory the trainer is launched from."""
    pinned = tmp_path / "pinned"
    pinned.mkdir()
    _write_config(pinned, "build/va-neural-host.dll")

    elsewhere = tmp_path / "elsewhere"
    elsewhere.mkdir()
    monkeypatch.chdir(elsewhere)
    monkeypatch.setattr(env_module, "_host_config_paths", lambda: [pinned / env_module.HOST_CONFIG_NAME])

    assert env_module._default_host_binary() == (pinned / "build" / "va-neural-host.dll").resolve()


def test_unparsable_config_names_the_file(tmp_path):
    (tmp_path / env_module.HOST_CONFIG_NAME).write_text("{not json", encoding="utf-8")
    with pytest.raises(RuntimeError, match=env_module.HOST_CONFIG_NAME):
        env_module._default_host_binary()


def test_config_without_a_host_dll_key_falls_through(tmp_path, monkeypatch):
    (tmp_path / env_module.HOST_CONFIG_NAME).write_text(json.dumps({"note": "no path here"}), encoding="utf-8")
    monkeypatch.setattr(env_module, "_host_from_enclosing_checkout", lambda start=None: None)
    with pytest.raises(FileNotFoundError):
        env_module._default_host_binary()


# --- the property that makes the extraction safe -------------------------------------------------------

@pytest.mark.parametrize("depth", [1, 3, 6])
def test_checkout_is_found_by_marker_at_any_depth(tmp_path, depth):
    """The old code only worked at exactly one depth. A marker search works at all of them."""
    root = tmp_path / "checkout"
    root.mkdir()
    (root / "VortexArena.sln").write_text("", encoding="utf-8")

    nested = root.joinpath(*[f"level{i}" for i in range(depth)])
    nested.mkdir(parents=True)
    start = nested / "env.py"

    found = env_module._host_from_enclosing_checkout(start)
    assert found == root / env_module._HOST_BUILD_OUTPUT


def test_no_checkout_returns_none_rather_than_a_wrong_path(tmp_path):
    """The extracted-repository case: no marker anywhere above, so no guess is offered."""
    lonely = tmp_path / "neuralbotlab" / "va_neural"
    lonely.mkdir(parents=True)
    assert env_module._host_from_enclosing_checkout(lonely / "env.py") is None


def test_error_lists_every_way_to_supply_the_host(monkeypatch):
    monkeypatch.setattr(env_module, "_host_from_enclosing_checkout", lambda start=None: None)
    with pytest.raises(FileNotFoundError) as excinfo:
        env_module._default_host_binary()

    message = str(excinfo.value)
    assert env_module.HOST_ENV_VAR in message
    assert env_module.HOST_CONFIG_NAME in message
    assert "host_dll" in message and "remote=" in message


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-q"]))
