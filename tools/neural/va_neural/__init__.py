"""Training-side support for the Vortex Arena neural bots.

Nothing the game needs at runtime lives here: the shipped server loads a weight file and evaluates it with
its own 200-line MLP (``src/VortexArena.Server/Bot/Neural/PolicyNetwork.cs``). This package is the other
half — the trainer, the env client, and the layout mirror that keeps the two honest.
"""

from . import layout  # noqa: F401
