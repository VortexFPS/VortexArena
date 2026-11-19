"""Check both PulseAudio monitors against the scripted row 93 actions."""
import argparse
import hashlib
import math
from pathlib import Path
import subprocess


SAMPLE_SHA256 = "a15d93c538d40f49c2f96ac37a550f86ad4691645dc20f4c046e6114860709d4"


def expected_windows(timeline, engine, phase):
    events = {}
    for line in timeline.splitlines():
        stamp, _, event = line.partition(" ")
        try:
            seconds = float(stamp)
        except ValueError:
            continue
        if not math.isfinite(seconds):
            raise ValueError("non-finite timestamp in launcher timeline")
        events.setdefault(event, []).append(seconds)

    def moment(action):
        stamps = events.get(action, [])
        if len(stamps) != 1:
            raise ValueError(f"expected one '{action}' in the launcher timeline, found {len(stamps)}")
        return stamps[0]

    if any(key.startswith("scenario ") for key in events):
        moment(f"scenario {engine} {phase}")
    origin = moment("audio recording")
    pickup = moment("picker F7" if engine == "stock" else "picker cmd teleporttotarget probe_shooter")
    end = moment("done")
    if not origin < pickup < end:
        raise ValueError("pickup fell outside the recording")
    if (engine, phase) == ("stock", "early"):
        raise ValueError("stock records early loss in the timed run")

    # Each boundary is a logged launcher input. The monitors start immediately
    # before the audio marker and the commands take effect after their input.
    windows = [("before pickup", 0.0, 0)]
    if phase == "overlap":
        hold = moment("picker F9" if engine == "stock" else "picker cmd give unlimited_superweapons")
        release = moment("picker F10" if engine == "stock" else "picker cmd give 0 unlimited_superweapons")
        if release - hold <= 10:
            raise ValueError("unlimited hold did not span the ten-second pickup duration")
        windows.extend([("pickup", pickup - origin, 0), ("unlimited hold", hold - origin, 0),
                        ("release", release - origin, 1)])
    elif engine == "stock" or phase == "early":
        drop = moment("picker F8" if engine == "stock" else "picker cmd give 0 fireball")
        windows.extend([("before early loss", pickup - origin, 6 if engine == "stock" else 0),
                        ("early loss", drop - origin, 1)])
    else:
        windows.append(("countdown", pickup - origin, 6))
    starts = [start for _, start, _ in windows]
    if starts != sorted(set(starts)) or starts[-1] >= end - origin:
        raise ValueError("capture actions are out of order")
    return windows, end - origin


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("engine", choices=["stock", "native"])
    parser.add_argument("phase", choices=["timed", "early", "overlap"])
    parser.add_argument("capture", type=Path)
    parser.add_argument("--timeline", type=Path, help="launcher stdout for a capture made before timeline.log")
    parser.add_argument("--matcher", required=True, type=Path, help="compiled match-cues.c")
    parser.add_argument("--sample", type=Path, default=Path("/tmp/vortex-parity-base/sound/misc/poweroff.ogg"))
    args = parser.parse_args()
    if hashlib.sha256(args.sample.read_bytes()).hexdigest() != SAMPLE_SHA256:
        parser.error("the reference sample differs from the pinned poweroff Ogg")
    timeline = args.timeline or args.capture / "timeline.log"
    windows, end = expected_windows(timeline.read_text(encoding="utf-8"), args.engine, args.phase)
    errors = []
    for role in ("picker", "peer"):
        monitor = args.capture / f"{role}.raw"
        size = monitor.stat().st_size
        if size == 0 or size % 4:
            raise ValueError(f"{monitor} is empty or ends mid-frame")
        # The launcher starts the recorders just before the first marker and
        # signals them immediately after done. Allow one tenth of a second
        # for monitor buffering and the independent wall/audio clocks.
        if abs(size / (48000 * 4) - end) > 0.1:
            raise ValueError(f"{monitor} duration differs from the recorded scenario end")
        output = subprocess.check_output([args.matcher.resolve(), args.sample, monitor], text=True)
        matches = [float(line.split()[0]) for line in output.splitlines() if " s  corr " in line]
        for index, (label, start, expected) in enumerate(windows):
            stop = windows[index + 1][1] if index + 1 < len(windows) else end
            observed = [t for t in matches if start <= t < stop]
            print(f"{role}, {label}: {len(observed)} of {expected} cues {observed}")
            if len(observed) != expected:
                errors.append(f"{role}, {label}")
    if errors:
        raise SystemExit("Cue count mismatch: " + ", ".join(errors))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, subprocess.CalledProcessError) as error:
        raise SystemExit(str(error))
