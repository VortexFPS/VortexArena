"""Verify the stock VM observations and matching native ENet events."""
from pathlib import Path
from math import dist
import re
import sys

CASES = ("unlimited", "old_stuck", "other_owner", "at_limit", "evict_oldest", "collateral")
BLASTS = (0, 0, 0, 0, 1, 3)
ORB_COUNTS = (1, 2, 3, 4, 4, 2)


def phases(path, marker):
    log = re.sub(r"\x1b\[[0-9;]*[A-Za-z]", "", path.read_text(errors="replace"))
    parts = re.split(rf"^{marker} (\w+)[ \t]*$", log, flags=re.M)
    phases = dict(zip(parts[1::2], parts[2::2]))
    assert tuple(phases) == CASES, (path, tuple(phases))
    return phases


def normalize(text):
    return " ".join(text.split())


def stock_orbs(log):
    orbs = {}
    for record in re.split(r"^server EDICT ", log, flags=re.M)[1:]:
        if not re.search(r"^classname +electro_orb$", record, re.M):
            continue
        fields = dict(re.findall(r"^(owner|origin|creationtime|nextthink|move_movetype) +(.+)$", record, re.M))
        orbs[int(record.partition(":")[0])] = fields
    return orbs


stock = Path(sys.argv[1])
server = phases(stock / "server.log", "ELECTRO_BEGIN")
alpha = phases(stock / "Alpha.log", "ELECTRO_BEGIN")
beta = phases(stock / "Beta.log", "ELECTRO_BEGIN")
all_orbs = {case: stock_orbs(server[case]) for case in CASES}

print("Stock pinned SVQC and both SDL clients")
for case, blasts, orb_count in zip(CASES, BLASTS, ORB_COUNTS):
    assert len(all_orbs[case]) == orb_count, (case, all_orbs[case])
    assert server[case].count(" : W_Electro_Explode :") == blasts, case
    assert alpha[case].count(" : Electro_wr_impacteffect :") == blasts, case
    assert beta[case].count(" : Electro_wr_impacteffect :") == blasts, case
    print(f"{case} server={blasts} Alpha={blasts} Beta={blasts}")
    for eid, orb in all_orbs[case].items():
        print(f"  #{eid} {normalize(orb['owner'])} origin={orb['origin']} "
              f"created={orb.get('creationtime', 'unlisted')} deadline={orb['nextthink']} "
              f"movetype={orb.get('move_movetype', '0')}")

def added(before, after):
    [orb] = set(all_orbs[after]) - set(all_orbs[before])
    return orb


def position(orb):
    return tuple(map(float, orb["origin"].strip("'").split()))

unlimited = next(iter(all_orbs["unlimited"]))
old = added("unlimited", "old_stuck")
foreign = added("old_stuck", "other_owner")
middle = added("other_owner", "at_limit")
new = added("at_limit", "evict_oldest")
assert "creationtime" not in all_orbs["unlimited"][unlimited]
old_state = all_orbs["at_limit"][old]
middle_state = all_orbs["at_limit"][middle]
assert float(old_state["creationtime"]) < float(middle_state["creationtime"])
assert float(old_state["nextthink"]) > float(middle_state["nextthink"])
assert old_state.get("move_movetype", "0") == "0"
assert old not in all_orbs["evict_oldest"]
assert dist(position(old_state), position(all_orbs["evict_oldest"][new])) < 2
assert all_orbs["other_owner"][foreign]["owner"] == "entity 2"
assert set(all_orbs["collateral"]) == {unlimited, foreign}
assert server["evict_oldest"].count(" : LimitedChildrenRubble :") == 1
assert server["collateral"].count(" : LimitedChildrenRubble :") == 2

print("\nNative normal fire gate and both negotiated ENet receivers")
native = phases(Path(sys.argv[2]), "ORBS")
for case, blasts, orb_count in zip(CASES, BLASTS, ORB_COUNTS):
    assert "ERROR:" not in native[case], case
    alpha_origins = re.findall(r"^BLAST receiver=Alpha origin=(.+)$", native[case], re.M)
    beta_origins = re.findall(r"^BLAST receiver=Beta origin=(.+)$", native[case], re.M)
    assert alpha_origins == beta_origins and len(alpha_origins) == blasts, case
    orbs = re.findall(r"^ORB (.+)$", native[case], re.M)
    assert len(orbs) == orb_count, case
    print(f"{case} Alpha={blasts} Beta={blasts} origins={alpha_origins}")
    for orb in orbs:
        print(f"  {orb}")
