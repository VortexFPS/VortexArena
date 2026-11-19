"""Check the paired engine traces written by reproduce-teamradar.sh."""
import argparse
import math
import subprocess
from pathlib import Path


def rows(path, label):
    return [line[len(label) + 1:].split() for line in path.read_text().splitlines()
            if line.startswith(label + " ")]


def geometry(root):
    frames = []
    for row in rows(root / "client.log", "RADAR_GEOM"):
        mouse, angle, zoom, size, x, y, z, lo, hi, top, ax, ay, bx, by = (
            float(value) for value in row if value != "|")
        if (x, y, lo, hi, top) == (-80, -208, -2400, 1200, 1800):
            frames.append((mouse, angle, zoom, size, z, ax, ay, bx, by))
    return frames


def compare(stock, native):
    for name, root in (("Stock", stock), ("Native", native)):
        sent = rows(root / "server.log", "RADAR_SEND")
        recv = rows(root / "client.log", "RADAR_RECV")
        draw = rows(root / "client.log", "RADAR_DRAW")
        assert [int(row[0]) for row in sent] == list(range(1, 37)), (name, "server count")
        assert len(recv) == 36, (name, "receive count", len(recv))
        slots = [int(row[0 if name == "Stock" else 1]) for row in recv]
        assert slots == [n % 32 for n in range(1, 37)], (name, "ring wrap")
        if name == "Native":
            assert [int(row[0]) for row in recv] == list(range(1, 37))
            assert [int(row[2]) for row in recv] == [min(n, 32) for n in range(1, 37)]
        pulse_counts = [int(row[2]) for row in draw]
        assert max(pulse_counts) == 3, (name, "overlap")
        assert pulse_counts[-1] == 0, (name, "final expiry")
        print(f"{name}: {len(sent)} sent, {len(recv)} recorded, slots 31→0→1, peak 3 active pulses")

    frames = [geometry(root) for root in (stock, native)]
    for name, match in (
        ("North, fitted", lambda f: f[0] == 0 and f[1] == 6.28 and f[3] > 500),
        ("Yaw 37°, normal", lambda f: f[0] == 0 and -.93 <= f[1] <= -.92 and f[2] == 0 and f[5] < 450),
        ("Yaw 37°, fitted", lambda f: f[0] == 0 and -.93 <= f[1] <= -.92 and f[2] == 1 and f[5] > 450),
        ("Yaw 37°, fitted and mirrored", lambda f: f[0] == 0 and -.93 <= f[1] <= -.92 and f[2] == 1 and f[5] < 450),
        ("Mirrored picker", lambda f: f[0] == 1 and -.93 <= f[1] <= -.92 and f[2] == 1 and f[5] < 450),
    ):
        a, b = [next((f for f in side if match(f)), None) for side in frames]
        assert a and b, (name, a, b)
        assert max(abs(x - y) for x, y in zip(a[5:], b[5:])) <= 0.1, (name, a, b)
        assert abs(a[3] - b[3]) <= 0.1, (name, a[3], b[3])
        print(f"{name}: scale {a[3]:.2f}/{b[3]:.2f}, waypoint pixels {a[5:]}/{b[5:]}")

    for side in frames:
        intermediate = [f[2] for f in side if f[0] == 0 and -.93 <= f[1] <= -.92 and 0 < f[2] < 1]
        assert intermediate, "held zoom did not pass through an intermediate fraction"
        print("Intermediate zoom samples:", intermediate[:8])

    for root, side in zip((stock, native), frames):
        clicks = rows(root / "server.log", "RADAR_CLICK")
        assert len(clicks) == 1, clicks
        x, y, z = map(float, clicks[0])
        theta = math.radians(-53)
        scale = 420 / (1.05 * math.hypot(3600, 2800))
        dx, dy = -77 / scale, -41 / scale
        expected_x = -600 + dx * math.cos(theta) - dy * math.sin(theta)
        expected_y = 400 + dx * math.sin(theta) + dy * math.cos(theta)
        assert math.hypot(x - expected_x, y - expected_y) < .1, clicks
        assert any(f[0] == 1 and abs(z - f[4]) < .02 for f in side), clicks
        print("Server pick:", " ".join(clicks[0]))


def compare_followup(stock, native):
    def pair(root, label):
        (reading,) = rows(root / "followup.log", label)
        return tuple(map(float, reading))

    raw = pair(stock, "RADAR_RAW")
    bounds = pair(stock, "RADAR_BOUNDS")
    assert raw == pair(native, "RADAR_RAW") and bounds == pair(native, "RADAR_BOUNDS")
    assert raw == (-3008, -1344, 1600, 3264), ("fixture BSP bounds", raw)
    assert len(bounds) == 4
    expected = (-2944.03125, -1280.03125, 1536.03125, 3200.03125)
    assert all(abs(a - b) <= 0.001 for a, b in zip(bounds, expected)), ("swept bounds", bounds)
    print("Both engines' raw and collision bounds:", raw, bounds)

    def pixel(root, screenshot, x, y):
        return tuple(subprocess.check_output(["convert", str(root / f"{screenshot}.png"),
                                             "-crop", f"1x1+{x}+{y}", "+repage",
                                             "-alpha", "off", "-depth", "8", "rgb:-"]))

    for name, root in (("Stock", stock), ("Native", native)):
        black = [pixel(root, shot, 300, 130) for shot in ("blend-backdrop", "blend-screen")]
        grey = [pixel(root, shot, 550, 280) for shot in ("blend-backdrop", "blend-screen")]
        assert max(abs(a - b) for a, b in zip(*black)) <= 3, (name, black)
        for dst, result in zip(*grey):
            # HUD foreground 0.5 times each engine's panel fade. Native's panel alpha is 0.9.
            shade = 0.45 if name == "Native" else 0.5
            expected = dst + (255 - dst) * (128 / 255) * shade
            assert abs(expected - result) <= 6, (name, grey, expected)
        print(f"{name} black pixel {black[0]} → {black[1]}, grey pixel {grey[0]} → {grey[1]}")

    log = native / "followup.log"
    first, clear, fresh = rows(log, "RADAR_PACKET")
    assert first == ["1", "1", "False", "16"]
    assert clear[1:] == ["0", "True", "16"] and int(clear[0]) > 4
    assert fresh[1:] == ["1", "False", "16"] and int(fresh[0]) > int(clear[0])
    assert rows(log, "RADAR_LOSS") == [["3"]]
    assert rows(log, "RADAR_STALE") == [["1", clear[0], "0"]]
    assert rows(log, "RADAR_STATE") == [[r[0], r[1], r[3]] for r in (first, clear, fresh)]
    print(f"Native held time at 16, dropped ≥3 updates, received reliable empty {clear[0]}, "
          f"rejected delayed update 1, and accepted newer update {fresh[0]}.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--followup", action="store_true")
    parser.add_argument("stock", type=Path)
    parser.add_argument("native", type=Path)
    args = parser.parse_args()
    if not __debug__:
        parser.error("Use ordinary Python so the verification assertions run")
    if args.followup:
        compare_followup(args.stock, args.native)
    else:
        compare(args.stock, args.native)
