"""Check the paired moving-target snapshots and recipient logs."""
from pathlib import Path
import re
import sys

stock = Path(sys.argv[1])
native = Path(sys.argv[2]).read_text()
server = (stock / "server.log").read_text(errors="replace")
parts = re.split(r"^ORBS (\w+) *$", server, flags=re.M)
phases = dict(zip(parts[1::2], parts[2::2]))
expected = ("attached", "moved", "dead", "reattached", "disconnected")
assert tuple(phases) == expected
states = {}
stale = None

print("Stock pinned server")
for phase, log in phases.items():
    matches = [edict for edict in re.split(r"^server EDICT ", log, flags=re.M)[1:]
               if re.search(r"^classname +electro_orb$", edict, re.M)]
    if phase == "disconnected":
        print(f"{phase}: {len(matches)} remaining")
        if not matches:
            continue
    [edict] = matches
    fields = dict(re.findall(r"^(origin|creationtime|death_time|nextthink|move_movetype|owner|aiment) +(.+)$",
                             edict, re.M))
    states[phase] = (edict.partition(":")[0], fields)
    print(f"{phase}: edict {states[phase][0]} {fields}")
    assert fields["owner"] == "entity 1"
    assert fields["aiment"] == "entity 2"
    assert fields["move_movetype"] == ("6" if phase in ("dead", "disconnected") else "12")
    if phase == "disconnected":
        stale = fields

initial_id, initial = states["attached"]
for phase in ("moved", "dead"):
    assert states[phase][0] == initial_id
    assert states[phase][1]["creationtime"] == initial["creationtime"]
    assert states[phase][1]["death_time"] == initial["death_time"]
assert states["moved"][1]["origin"] != initial["origin"]
assert states["dead"][1]["nextthink"] == initial["death_time"]
assert float(states["reattached"][1]["creationtime"]) > float(initial["creationtime"])
if stale is not None:
    assert stale["creationtime"] == states["reattached"][1]["creationtime"]
    assert stale["nextthink"] == states["reattached"][1]["death_time"]
eviction = server.split("\nELECTRO_BEGIN reattached", 1)[1]
detonations = re.findall(r" : W_Electro_Explode :", eviction)
assert len(detonations) == 1
assert eviction.count(" : LimitedChildrenRubble :") == 1

for client in ("Alpha", "Beta"):
    log = (stock / f"{client}.log").read_text(errors="replace").split("\nELECTRO_BEGIN reattached", 1)[1]
    count = log.count(" : Electro_wr_impacteffect :")
    assert count == 1, (client, count)
    print(f"{client}: {count} impact at cap eviction")
    if client == "Alpha":
        plainly = re.sub(r"\x1b\[[0-9;]*[A-Za-z]", "", log)
        _, after_disconnect = plainly.split("Beta disconnected", 1)
        assert " : Electro_wr_impacteffect :" not in after_disconnect
        print("Alpha: 0 impacts after Beta's disconnect notification")

print("\nNative negotiated ENet clients")
assert "ERROR:" not in native
for line in native.splitlines():
    if line.startswith(("PASS ", "FOLLOW ", "BLAST ", "ORB ")):
        print(line)
assert native.count("BLAST receiver=") == 2
assert "PASS disconnect removes the attachment without another impact" in native
assert "PASS the deleted orb leaves the cap slot available without a forced detonation" in native
