# Native casing and gib limits

The client reads each cvar at spawn. Both default to 100. Lowering a cap leaves existing models until the next spawn, then removes the oldest across all batches of that type. A cap of zero clears that type on the next spawn. Raising the cap allows more models as they spawn. The native implementation bounds each ordinary pool at 1024 and keeps GPU instance buffers fixed. Raptor shell fragments use a separate pool.

The .NET 8 Godot Compatibility probe checked actual instances and positions after live edits. It used the native casing effect dispatcher, the registered corpse model-gib effect, and meshes loaded from shipped assets. The probe drew two casings and two gib models in the viewport. It exercised the corpse damage and client dispatch paths in one process. A multiplayer packet capture and alternate gentle or particle gib effects were outside this check. [Probe log](probe.txt) · [Viewport capture](viewport.png)

## Fixed-step motion fixture

The council repeat passed 42 assertions. With a 0.03125-second tic, the last representable float before the boundary kept the committed pose, and the exact boundary advanced it once. The same run rendered the shipped meshes at 800 × 600. [Motion probe output](motion-probe.txt) · [Rendered viewport](motion-viewport.png)

The probe now also exercises the native render callbacks with deterministic frame schedules at 30, 60 and 144 FPS. It compares positions and rotation bases for casings, an ordinary model gib and three Raptor fragments after their normal client spawns. Separate assertions check residual gravity and rotation, the 32-tic catch-up budget, the five-bump toss cap, a live switch to zero ticrate, strict and sloppy traces against synthetic world brushes, the model-gib hull, casing sound and startsolid handling, and NOIMPACT removal.

The Godot Compatibility viewport check loads the shipped casing and gib meshes. It covers native client pools and static collision. Remote turret gibs, projectile prediction, moving brushes and side-by-side stock renderer behavior are outside the fixture.

From an already restored checkout, stage the scene and source in a temporary copy. Set `GODOT` to a Godot 4.6.3 Mono executable and `DOTNET_ROOT` to an installed .NET 8 runtime.

```sh
stage=$(mktemp -d /tmp/va-debris-motion.XXXXXX)
cp -a --reflink=auto game src .godot "$stage"
cp VortexArena.csproj Directory.Build.props global.json nuget.config project.godot \
  Main.cs Main.cs.uid Main.tscn "$stage"
ln -s "$PWD/data" "$stage/data"
cp planning/parity/rubble-limits/RubbleLimitProbe.cs "$stage/game/client/"
cp planning/parity/rubble-limits/RubbleLimitProbe.tscn.txt "$stage/RubbleLimitProbe.tscn"
mkdir -p "$stage/user"/{data,config,cache}
(
  cd "$stage" || exit
  dotnet build VortexArena.csproj --no-restore > build.log 2>&1 &&
  XDG_DATA_HOME="$stage/user/data" XDG_CONFIG_HOME="$stage/user/config" \
    XDG_CACHE_HOME="$stage/user/cache" LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS=2 \
    RUBBLE_CAPTURE_PATH="$stage/viewport.png" \
    xvfb-run -a "$GODOT" --path . --audio-driver Dummy \
      --rendering-method gl_compatibility --render-thread safe \
      --resolution 800x600 res://RubbleLimitProbe.tscn > probe.log 2>&1
)
```

## Ordinary corpse effects

Run `bash planning/parity/rubble-limits/reproduce-network-order.sh rubble` with `GODOT` set to the Godot 4.6.3 Mono executable. The 64 viewport and simulation checks cover the mounted particle atlas, species-specific showers, gentle and particle-only substitutions, model counts, fractional suppression, and live velocity, rotation and lifetime controls. The fixture samples active particle slots. It also exercises the existing casing and Raptor pools. [Probe output](gib-options.txt) · [Paired lethal-hit comparison](gib-options-live/README.md)

Native uses a different random sequence and caps shower sampling at 1,024 calls per splash. The paired shots used human models. Model trails, chunk audio and end-of-life model fading remain open.
