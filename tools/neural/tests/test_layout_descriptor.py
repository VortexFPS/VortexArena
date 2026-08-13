"""The C#/Python layout contract, and the skew the old size check could not see.

The handshake used to compare observation and action LENGTHS and nothing else. That check is blind to a
size-preserving change: swap two equal-width sections, or repurpose the floats inside one, and both ends
still agree on 302 while disagreeing about what the 302 numbers mean. Nothing crashes. The network goes on
producing plausible actions from misread columns, and the only symptom is a run that stops improving for no
visible reason.

``layout.descriptor()`` carries every section's name and width in vector order, so that change is visible.
The literal asserted below is also asserted from C# in
``tests/VortexArena.Tests/NeuralBotTests.cs::LayoutDescriptorMatchesTheCrossLanguageContract``. That is the
whole mechanism: neither language reads the other's source, so the literal IS the contract, and a layout
change that updates only one side fails the other side's suite.

Run:  python -m pytest tools/neural/tests -q
  or: python tools/neural/tests/test_layout_descriptor.py
"""
from __future__ import annotations

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from va_neural import layout  # noqa: E402

# Kept as one literal rather than built from layout's own constants: a descriptor compared against itself
# proves nothing. This string is the agreement with the C# host.
CONTRACT = (
    "obs:proprio=15,weapon=12,goal=11,aim=4,history=8,prev_action=8,navfield=96,navfield_up=48,"
    "route=24,features=64,trace_fan=12"
    "|act:move=9,jump=2,crouch=2,attack1=2,attack2=2,weapon=4,yaw=1,pitch=1"
    "|wire=8"
)
CONTRACT_FINGERPRINT = 0xEAA83A60E3C8703F


def test_descriptor_matches_the_cross_language_contract():
    assert layout.descriptor() == CONTRACT


def test_fingerprint_is_reproducible():
    """FNV-1a is hand-rolled in both languages so this number is stable across processes and versions.

    A drift here while the descriptor still matches means the two hash implementations have diverged,
    not the layout.
    """
    assert layout.fingerprint(CONTRACT) == CONTRACT_FINGERPRINT
    assert layout.fingerprint() == CONTRACT_FINGERPRINT


def test_descriptor_covers_the_whole_observation():
    """Section widths account for every float, so a section added but left out of the descriptor is caught."""
    obs = CONTRACT.split("|")[0][len("obs:"):]
    assert sum(int(part.split("=")[1]) for part in obs.split(",")) == layout.OBS_SIZE


def test_verify_accepts_a_matching_host():
    layout.verify(layout.OBS_SIZE, layout.WIRE_ACTION_SIZE, layout.descriptor())


def test_verify_rejects_a_size_preserving_reorder():
    """The case the size check is structurally blind to.

    ``history`` and ``prev_action`` are both 8 floats wide, so swapping them leaves the observation at 302
    and the wire action at 8. Every size assertion still passes; only the descriptor notices.
    """
    swapped = layout.descriptor().replace("history=8,prev_action=8", "prev_action=8,history=8")
    assert swapped != layout.descriptor()

    with pytest.raises(RuntimeError) as excinfo:
        layout.verify(layout.OBS_SIZE, layout.WIRE_ACTION_SIZE, swapped)

    message = str(excinfo.value)
    assert "reordered or renamed" in message
    # The error has to name the section; a bare "layouts differ" leaves someone diffing eleven constants.
    assert "prev_action" in message and "history" in message


def test_verify_rejects_a_changed_section_width():
    narrowed = layout.descriptor().replace("goal=11", "goal=10")
    with pytest.raises(RuntimeError) as excinfo:
        # Sizes are passed as the host's own, so this isolates the descriptor check from the length check.
        layout.verify(layout.OBS_SIZE, layout.WIRE_ACTION_SIZE, narrowed)
    message = str(excinfo.value)
    assert "goal" in message
    assert "10 floats on the host but 11 here" in message


def test_verify_rejects_a_renamed_section():
    renamed = layout.descriptor().replace("trace_fan=12", "tracefan=12")
    with pytest.raises(RuntimeError) as excinfo:
        layout.verify(layout.OBS_SIZE, layout.WIRE_ACTION_SIZE, renamed)
    assert "tracefan" in str(excinfo.value)


def test_verify_rejects_an_extra_section():
    extended = layout.descriptor().replace("|wire=8", ",extra=4|wire=8")
    with pytest.raises(RuntimeError) as excinfo:
        layout.verify(layout.OBS_SIZE, layout.WIRE_ACTION_SIZE, extended)
    assert "extra" in str(excinfo.value)


def test_verify_still_reports_a_plain_size_mismatch_first():
    """A genuine length change should give the blunt message, not a descriptor diff."""
    with pytest.raises(RuntimeError, match="observation layout skew"):
        layout.verify(layout.OBS_SIZE + 1, layout.WIRE_ACTION_SIZE, layout.descriptor())


if __name__ == "__main__":
    raise SystemExit(pytest.main([__file__, "-q"]))
