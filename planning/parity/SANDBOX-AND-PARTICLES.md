# Sandbox carry and Original particles verification

## Sandbox carry

- The default V binding drives `+button8`. The binding test confirms press, release, and focus-loss reset. The input codec test serializes and deserializes an `InputCommand` with `Drag`, `Attack`, and `Jump` set and confirms that the button mask survives the wire format.
- The server-side mutator test uses two player entities with distinct owner IDs and a controlled object hit. With `g_sandbox_editor_free 1`, it rejects a non-owner and accepts the owner. It checks turn-driven velocity and yaw, then confirms that release restores movement type and gravity and clears velocity. With free editing set to 2, it accepts the non-owner. Enabling `g_sandbox_readonly` during the carry releases the object and blocks a new grab.
- A native Keybench listen-server session completed a local handshake and logged the sandbox spawn command. Those logs and captures do not establish successful visual carry or a two-client ownership check. The binding, wire codec, and mutator were exercised independently. Subsequent paired sessions verified owner and guest carry, release and the read-only transition. [Matched recordings](scripted-sandbox-buffs/README.md)

## Original particles

- Native Godot 4.6.3 Mono logs from Keybench and Darkzone record the OpenGL compatibility renderer on Mesa llvmpipe, `vortex/original/effectinfo.txt` parsed with 301 effects, the particlefont atlas loaded, and `backend=original (cl_particles_modern=0)`. Both runs verify the Original asset pair and backend initialization in the native client.
- `ParticleDarkPlacesTests` exercises DarkPlaces simulation fixtures for impacts, explosions, trails, smoke, and emitters, plus spark quad orientation and frame-rate-independent map emission.
- The Keybench log records console input for all three particle modes, but contains positive backend evidence only for Original. Darkzone was terminated after initialization while assets were loading. Neither run establishes rendered effect-by-effect parity against a matched Xonotic capture or a successful live switch to Mixed and Modern.

## Reproduction and evidence

The focused run passed 40 of 40 tests from the built test assembly. The host has .NET 10.0.15, so the net8.0 testhost used major runtime roll-forward.

```sh
DOTNET_ROLL_FORWARD=Major dotnet vstest tests/VortexArena.Tests/bin/Debug/net8.0/VortexArena.Tests.dll \
  '--TestCaseFilter:FullyQualifiedName~SandboxDragTests|FullyQualifiedName~InputCommandImpulseTests|FullyQualifiedName~ParticleDarkPlacesTests'
```

Native session logs from this worktree are `/tmp/vortex-parity-live.log` (Keybench) and `/tmp/vortex-parity-darkzone-live.log` (Darkzone).
