"""Check stock branches and native hit, chunk and corpse effects for each lethal shot."""
from collections import Counter
from pathlib import Path
import re
import sys

ANSI = re.compile(r"\x1b\[[0-9;]*[A-Za-z]")
CASES = ("default", "gentle1", "gentle2", "gentle3", "particles", "ekg", "nogibs")
PREFIX = {"gentle1": "morphed_", "gentle2": "", "gentle3": "happy_", "particles": "particlegibs_"}


def trials(path):
    log = ANSI.sub("", Path(path).read_text(errors="replace"))
    fields = re.split(r"^GIB_TRIAL (\w+) *$", log, flags=re.M)
    return dict(zip(fields[1::2], fields[2::2]))


def format_counts(values):
    counts = Counter(values)
    return ", ".join(f"{name}×{count}" for name, count in sorted(counts.items())) or "none"


def requests(lines):
    return Counter(line.split(" blocks=")[0] for line in lines)


def expected_requests(case):
    """Effect names, input counts and repetitions for the pinned human fixture at quality 1."""
    if case == "nogibs":
        return [], []
    if case in ("default", "ekg"):
        return (["blood count=1000" if case == "ekg" else "blood count=200"] * 2,
                ["bloodshower count=0.1" if case == "ekg" else "bloodshower count=0.02"] * 50)
    prefix = {"gentle1": "morphed_", "gentle2": "", "gentle3": "happy_", "particles": "particlegibs_"}[case]
    return [prefix + "damage_hit count=200"] * 2, [prefix + "damage_dissolve count=1"]


def survivor_models(log):
    for edict in re.split(r"^client EDICT ", log, flags=re.M):
        if not re.search(r"^classname +gib$", edict, re.M):
            continue
        model = re.search(r"^model +models/gibs/(\S+)", edict, re.M)
        if model:
            yield model[1]


stock, toss, hit, native = map(trials, sys.argv[1:5])
print("Stock pinned CSQC")
for case in CASES:
    log = stock[case]
    assert "Beta was gunned down by Alpha's Shotgun" in log, case
    assert re.search(r"^client: \d+ entities$", log, re.M), case
    branch = re.findall(r"gibs\.qc:(\d+) : Net_Handle_net_gibsplash :", log)
    expected = [] if case == "nogibs" else ["269" if case in ("default", "ekg") else "309"]
    assert branch == expected, (case, branch)
    assert "Beta was gunned down by Alpha's Shotgun" in hit[case], case
    hit_branches = re.findall(r"gibs\.qc:(302|312) : Net_Handle_net_gibsplash :", hit[case])
    expected_hits = [] if case == "nogibs" else ["302" if case in ("default", "ekg") else "312"] * 2
    assert hit_branches == expected_hits, (case, hit_branches)
    print(f"{case:9} type-1 calls {branch or 'none'}; type-2 calls {hit_branches or 'none'}; "
          f"surviving model edicts {format_counts(survivor_models(log))}")

print("\nStock TossGib entry breakpoints")
for case in ("default", "ekg"):
    assert "Beta was gunned down by Alpha's Shotgun" in toss[case]
    calls = [int(line) for line in re.findall(r"gibs\.qc:(\d+) : Net_Handle_net_gibsplash :", toss[case])]
    count = sum(268 <= line <= 298 for line in calls)
    assert count == (12 if case == "default" else 52), (case, calls)
    assert calls.count(306) == 3, (case, calls)
    print(f"{case:9} type-1 model calls {count}; type-3 calls {calls.count(306)}; call sites {dict(sorted(Counter(calls).items()))}")

print("\nNative ENet observing client")
for case in CASES:
    log = native[case]
    assert f"PASS lethal player shot for {case}" in log
    kinds = ["Hit", "Chunk", "Hit", "Chunk", "Chunk", "Splash"]
    wire = re.findall(r"^WIRE (\w+) (.+)$", log, re.M)
    assert [kind for kind, _ in wire] == kinds, (case, wire)
    assert [int(re.search(r"count=(\d+)", line)[1]) for _, line in wire] == [200, 1, 200, 1, 1, 1]
    fields = re.split(r"^GIB_EVENT (\w+) [^\n]+$", log, flags=re.M)
    assert fields[1::2] == kinds, case
    segments = list(zip(fields[1::2], fields[2::2]))
    model_pattern = r"^GIB_MODEL models/gibs/(\S+)"
    chunk_paths = [path for kind, segment in segments if kind == "Chunk" for path in re.findall(model_pattern, segment, re.M)]
    assert chunk_paths == (["chunk.mdl"] * 3 if case in ("default", "ekg") else []), (case, chunk_paths)
    splash = segments[-1][1]
    model_paths = re.findall(model_pattern, splash, re.M)
    assert len(model_paths) == {"default": 12, "ekg": 52}.get(case, 0), case
    hit_calls = [line for kind, segment in segments if kind == "Hit" for line in re.findall(r"^GIB_PARTICLE (.+)$", segment, re.M)]
    splash_calls = re.findall(r"^GIB_PARTICLE (.+)$", splash, re.M)
    if case in PREFIX:
        assert requests(hit_calls) == {f"{PREFIX[case]}damage_hit count=200": 2}, (case, hit_calls)
        assert requests(splash_calls) == {f"{PREFIX[case]}damage_dissolve count=1": 1}, (case, splash_calls)
    elif case in ("default", "ekg"):
        hit_amount, shower_amount = (200, "0.02") if case == "default" else (1000, "0.1")
        assert requests(hit_calls) == {f"blood count={hit_amount}": 2}, (case, hit_calls)
        assert requests(splash_calls) == {f"bloodshower count={shower_amount}": 50}, (case, splash_calls)
    else:
        assert not hit_calls and not splash_calls, (case, hit_calls, splash_calls)
    expected_hits, expected_splash = expected_requests(case)
    for actual, expected in ((hit_calls, expected_hits), (splash_calls, expected_splash)):
        assert [call.split(" blocks=")[0] for call in actual] == expected, (case, actual)
    amounts = re.findall(r"^GIB_SPLASH (.+)$", splash, re.M)
    viewport = re.search(r"^VIEWPORT faithful active=(\d+) uploaded=(\d+)$", log, re.M)
    assert viewport, case
    print(f"{case:9} type-1 {len(model_paths)} model calls {format_counts(model_paths)}; model splash {amounts or 'none'}; "
          f"type-1 particles {format_counts(splash_calls)}; type-2 particles {format_counts(hit_calls)}; "
          f"type-3 models {format_counts(chunk_paths)}; frame active={viewport[1]} uploaded={viewport[2]}")
    if case == "default":
        print("Default decoded wire order and coordinates")
        for kind, line in wire:
            print(f"  {kind} {line}")

if len(sys.argv) == 6:
    print("\nNative Modern backend submissions (controlled floor, collisionless)")
    modern = trials(sys.argv[5])
    for case in CASES:
        log = modern[case]
        assert f"PASS lethal player shot for {case}" in log
        calls = re.findall(r"^MODERN_PARTICLE (.+)$", log, re.M)
        original = re.findall(r"^GIB_PARTICLE (.+)$", native[case], re.M)
        assert Counter(calls) == requests(original), (case, calls)
        print(f"{case:9} {format_counts(calls)}")
