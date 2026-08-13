"""Observation and action layout — the Python mirror of the C# definitions.

Every constant here has a counterpart in ``src/VortexArena.Server/Bot/Neural/``:

    OBS_*      <- NeuralObservation.cs
    ACT_*      <- NeuralAction.cs (ActionSpace) and TrainingEnv.cs (ActionEncoding)
    WEIGHTS_*  <- PolicyNetwork.cs

They are checked against the running host at handshake: the host reports its observation and action sizes
and :func:`verify` raises if they disagree. That check is the whole reason this file states the sizes
rather than deriving them from whatever the socket sent.

A silent layout skew is the failure mode worth engineering against. Nothing crashes: the network keeps
producing plausible actions from misread inputs, and the only symptom is a policy that stops improving for
no visible reason.
"""

from __future__ import annotations

# --- observation sections (NeuralObservation.cs) ---
OBS_PROPRIO = 15
OBS_WEAPON = 12
OBS_GOAL = 11
OBS_AIM = 4
OBS_HISTORY = 8
OBS_PREV_ACTION = 8

# NavField.cs: 3 radii x 8 directions x (height, clearance, hazard, route delta)
NAVFIELD_RINGS = 3
NAVFIELD_DIRECTIONS = 8
OBS_NAVFIELD = NAVFIELD_RINGS * NAVFIELD_DIRECTIONS * 4

# NavField.cs SampleRingAbove: the 2 outer radii x 8 directions x (height above, clearance, hazard)
NAVFIELD_UP_RINGS = 2
OBS_NAVFIELD_UP = NAVFIELD_UP_RINGS * NAVFIELD_DIRECTIONS * 3

# NeuralObservation.cs route ribbon: 6 geodesic samples x (offset 2, rel height, hazard)
ROUTE_SAMPLES = 6
OBS_ROUTE = ROUTE_SAMPLES * 4

# MapFeatures.cs: 4 nearest x (dir 3, log distance 1, kind one-hot 7, exit dir 3, transit 1, state 1)
FEATURES_OBSERVED = 4
FEATURES_FLOATS_EACH = 16
OBS_FEATURES = FEATURES_OBSERVED * FEATURES_FLOATS_EACH

# NeuralObservation.cs: 6 box sweeps x (fraction, normal.z)
TRACE_FAN_RAYS = 6
OBS_TRACE_FAN = TRACE_FAN_RAYS * 2

OBS_SIZE = (
    OBS_PROPRIO
    + OBS_WEAPON
    + OBS_GOAL
    + OBS_AIM
    + OBS_HISTORY
    + OBS_PREV_ACTION
    + OBS_NAVFIELD
    + OBS_NAVFIELD_UP
    + OBS_ROUTE
    + OBS_FEATURES
    + OBS_TRACE_FAN
)

# Section offsets, useful for slicing an observation when debugging a run.
OFF_PROPRIO = 0
OFF_WEAPON = OFF_PROPRIO + OBS_PROPRIO
OFF_GOAL = OFF_WEAPON + OBS_WEAPON
OFF_AIM = OFF_GOAL + OBS_GOAL
OFF_HISTORY = OFF_AIM + OBS_AIM
OFF_PREV_ACTION = OFF_HISTORY + OBS_HISTORY
OFF_NAVFIELD = OFF_PREV_ACTION + OBS_PREV_ACTION
OFF_NAVFIELD_UP = OFF_NAVFIELD + OBS_NAVFIELD
OFF_ROUTE = OFF_NAVFIELD_UP + OBS_NAVFIELD_UP
OFF_FEATURES = OFF_ROUTE + OBS_ROUTE
OFF_TRACE_FAN = OFF_FEATURES + OBS_FEATURES

# --- action heads (ActionSpace in NeuralAction.cs) ---
# Categorical heads in output order, as (name, size). The network emits logits for all of them
# concatenated, then the two continuous view deltas.
CATEGORICAL_HEADS: list[tuple[str, int]] = [
    ("move", 9),      # 8 compass directions plus a null
    ("jump", 2),
    ("crouch", 2),
    ("attack1", 2),
    ("attack2", 2),
    ("weapon", 4),    # keep-current, blaster, crylink, devastator
]
CONTINUOUS_HEADS = ["yaw", "pitch"]

N_CATEGORICAL = sum(size for _, size in CATEGORICAL_HEADS)
N_CONTINUOUS = len(CONTINUOUS_HEADS)
ACTION_LOGITS = N_CATEGORICAL + N_CONTINUOUS

# ActionEncoding.Size in TrainingEnv.cs: six chosen indices then two continuous values in [-1,1].
WIRE_ACTION_SIZE = 8

# --- weight file (PolicyNetwork.cs) ---
WEIGHTS_MAGIC = 0x57505856  # "VXPW"
WEIGHTS_VERSION = 1
ACT_NONE, ACT_TANH, ACT_RELU = 0, 1, 2

# --- protocol (tools/neural/VortexArena.NeuralHost/Protocol.cs) ---
# 2 added the layout descriptor to HELLO_ACK. The host checks this value at HELLO, so a mismatched pair
# fails at the handshake with a readable message rather than later, with misaligned floats.
PROTOCOL_VERSION = 2

# Observation sections in vector order, as (name, width). The names match :func:`section_slices` below and
# ``NeuralLayoutDescriptor`` in C#, so a skew message reads the same on both sides of the socket.
OBS_SECTIONS: list[tuple[str, int]] = [
    ("proprio", OBS_PROPRIO),
    ("weapon", OBS_WEAPON),
    ("goal", OBS_GOAL),
    ("aim", OBS_AIM),
    ("history", OBS_HISTORY),
    ("prev_action", OBS_PREV_ACTION),
    ("navfield", OBS_NAVFIELD),
    ("navfield_up", OBS_NAVFIELD_UP),
    ("route", OBS_ROUTE),
    ("features", OBS_FEATURES),
    ("trace_fan", OBS_TRACE_FAN),
]


def descriptor() -> str:
    """The canonical layout description: every section's name and width, in vector order.

    Byte-identical to ``NeuralLayoutDescriptor.Build()`` in C#. Both sides derive it from their own live
    constants rather than restating a literal, so a section that changes width cannot change it in one
    language and stay silent in the other.
    """
    obs = ",".join(f"{name}={width}" for name, width in OBS_SECTIONS)
    act = ",".join(f"{name}={size}" for name, size in CATEGORICAL_HEADS)
    act += "," + ",".join(f"{name}=1" for name in CONTINUOUS_HEADS)
    return f"obs:{obs}|act:{act}|wire={WIRE_ACTION_SIZE}"


def fingerprint(text: str | None = None) -> int:
    """FNV-1a over the descriptor's UTF-8 bytes: the compact form for logs and run manifests.

    Hand-rolled to match the C# side. Python's ``hash()`` is salted per interpreter and .NET's
    ``GetHashCode`` is randomised per process, so neither can agree with the other or with itself across
    runs; FNV-1a gives the same number everywhere, forever.
    """
    data = (descriptor() if text is None else text).encode("utf-8")
    h = 0xCBF29CE484222325
    for b in data:
        h ^= b
        h = (h * 0x100000001B3) & 0xFFFFFFFFFFFFFFFF
    return h


def _parse(text: str) -> list[tuple[str, str, int]]:
    """(group, name, width) triples from a descriptor, in order."""
    out: list[tuple[str, str, int]] = []
    for group in text.split("|"):
        label, _, body = group.partition(":")
        if not body:
            # The trailing wire=N field is its own group and carries no label.
            name, _, value = group.partition("=")
            out.append(("wire", name, int(value)))
            continue
        for item in body.split(","):
            name, _, value = item.partition("=")
            out.append((label, name, int(value)))
    return out


def _first_difference(ours: str, theirs: str) -> str:
    """Name the first structural difference between two descriptors, in one sentence."""
    try:
        a, b = _parse(ours), _parse(theirs)
    except ValueError:
        return f"the host sent a descriptor this file cannot parse: {theirs!r}"

    for i in range(max(len(a), len(b))):
        if i >= len(a):
            group, name, width = b[i]
            return f"the host has an extra {group} section {name}={width} that this file does not know about"
        if i >= len(b):
            group, name, width = a[i]
            return f"this file expects a {group} section {name}={width} that the host does not produce"
        if a[i] == b[i]:
            continue
        (group, name, width), (_, their_name, their_width) = a[i], b[i]
        if name != their_name:
            return (f"{group} section {i} is {their_name}={their_width} on the host but {name}={width} "
                    f"here — the sections were reordered or renamed")
        return f"{group} section {name} is {their_width} floats on the host but {width} here"
    return "the descriptors differ in text but not in structure"


def verify(host_obs_size: int, host_action_size: int, host_descriptor: str) -> None:
    """Raise if the host's layout differs from this file's.

    Called at handshake, before a single sample is collected. Failing here costs a second; failing to fail
    here costs a training run.

    The two sizes are checked first because they produce the bluntest message. ``host_descriptor`` then
    catches what sizes structurally cannot: a section reordered, renamed, or repurposed at constant width.
    That skew leaves both ends agreeing on 302 floats while disagreeing about what the 302 numbers mean,
    and nothing crashes — the network goes on producing plausible actions from misread inputs.
    """
    if host_obs_size != OBS_SIZE:
        raise RuntimeError(
            f"observation layout skew: host says {host_obs_size} floats, "
            f"tools/neural/va_neural/layout.py says {OBS_SIZE}. "
            f"NeuralObservation.cs changed without this file following it."
        )
    if host_action_size != WIRE_ACTION_SIZE:
        raise RuntimeError(
            f"action layout skew: host says {host_action_size} floats per agent, "
            f"this file says {WIRE_ACTION_SIZE}. ActionEncoding.cs changed without this file following it."
        )
    ours = descriptor()
    if host_descriptor != ours:
        raise RuntimeError(
            "observation/action layout skew: " + _first_difference(ours, host_descriptor) + "\n"
            f"  host: {host_descriptor}\n"
            f"  here: {ours}\n"
            "  src/VortexArena.Server/Bot/Neural/ and this file have diverged."
        )


def section_slices() -> dict[str, slice]:
    """Named slices over one observation vector, for inspecting a rollout."""
    return {
        "proprio": slice(OFF_PROPRIO, OFF_PROPRIO + OBS_PROPRIO),
        "weapon": slice(OFF_WEAPON, OFF_WEAPON + OBS_WEAPON),
        "goal": slice(OFF_GOAL, OFF_GOAL + OBS_GOAL),
        "aim": slice(OFF_AIM, OFF_AIM + OBS_AIM),
        "history": slice(OFF_HISTORY, OFF_HISTORY + OBS_HISTORY),
        "prev_action": slice(OFF_PREV_ACTION, OFF_PREV_ACTION + OBS_PREV_ACTION),
        "navfield": slice(OFF_NAVFIELD, OFF_NAVFIELD + OBS_NAVFIELD),
        "navfield_up": slice(OFF_NAVFIELD_UP, OFF_NAVFIELD_UP + OBS_NAVFIELD_UP),
        "route": slice(OFF_ROUTE, OFF_ROUTE + OBS_ROUTE),
        "features": slice(OFF_FEATURES, OFF_FEATURES + OBS_FEATURES),
        "trace_fan": slice(OFF_TRACE_FAN, OFF_TRACE_FAN + OBS_TRACE_FAN),
    }
