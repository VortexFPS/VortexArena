# Native vehicle reticles

The Godot Compatibility probe rendered the native VehicleHud at 800 × 600 with the shipped art. It exercised the native remote mode selector with Raptor bomb and flare, all three Spiderbot modes, and the Bumblebee pilot. The projected positions and simulation timestamps were supplied by the fixture. The probe did not exercise an online vehicle session or the live Raptor trajectory calculation.

| Capture | What to look for |
| --- | --- |
| [Default size](default-all-paths.png) | Auxiliary slots 0–3 from left to right, burst reticle in the center, green dropmark below |
| [Size 1](size-one.png) | All three paths grow from the 0.5 default |
| [Size 0](zero-size.png) | The reticles disappear while the vehicle HUD remains |
| [Expired auxiliary markers](aux-expired.png) | Only refreshed slot 3 remains |
| [Released bomb](red-linger.png) | The dropmark is red and 25% larger |
| [Exit](exit.png) | The vehicle HUD and markers are gone |
| [Spiderbot guide](spiderbot-guide.png) and [Bumblebee heal](bumblebee-heal.png) | The two additional centered art styles |

The 34 viewport checks include alpha modulation, the lock colors and half-charge no-update threshold, expiry while simulation time advances, freeze and slow motion, scoreboard suppression, switching seats or vehicle kind, and exit. The viewport samples use a gray background to expose the dropmark's additive pass. [Probe output](probe.txt)

The application built successfully and 34 focused vehicle runtime, vehicle view, and vehicle sound tests passed on .NET 8. [Test output](dotnet8-tests.txt)

## Source trace

The Xonotic data checkout was pinned to `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`.

- `common/vehicles/cl_vehicles.qh` declares four auxiliary slots and a default size of 0.5. `cl_vehicles.qc:12–61` projects each update, multiplies marker alpha by `crosshair_alpha`, and clears the draw callback after 0.1 seconds. The marker keeps its alpha until expiry.
- `cl_vehicles.qc:63–118` initializes or clears markers during vehicle setup and assigns the burst art to both Bumblebee gunner slots. `cl_vehicles.qc:293–309` centers the mode's texture and uses the normal crosshair color calculation when vehicle colorization is enabled. `client/hud/crosshair.qc:165–204` defines that calculation.
- `common/vehicles/vehicle/raptor.qc:767–832` selects burst or rain, projects the live green dropmark, and keeps the last point for five seconds after its final refresh. The red marker has a 1.25 size multiplier. Both states draw an additive pass at 0.9 and a normal pass at 0.6, multiplied by crosshair alpha.
- `common/vehicles/vehicle/racer.qc:331–339` refreshes the lock blue below half charge, green above half charge, and red when fully locked. Exactly half charge sends no refresh. `common/vehicles/vehicle/bumblebee.qc:153–186, 545–546` supplies the lead, reload, and raygun colors.

## Reproduce

Run from the repository root with the Godot 4.6.3 .NET executable in `GODOT`, the .NET SDK on your path, and the .NET 8 runtime installed. If the runtime lives in a separate directory, set `DOTNET_ROOT` to its installation root for the Godot invocation. The probe creates a fresh scene directory and prints the new capture directory.

```sh
set -eu
probe_dir=$(mktemp -d game/hud/reticle-probe-XXXXXX)
capture_dir=$(mktemp -d)
trap 'rm -f "$probe_dir/VehicleReticleProbe.cs" "$probe_dir/VehicleReticleProbe.cs.uid" "$probe_dir/probe.tscn" "$probe_dir/probe.tscn.uid"; rmdir "$probe_dir"' EXIT
cp planning/parity/vehicle-reticles/VehicleReticleProbe.cs "$probe_dir/"
sed "s|game/hud/VehicleReticleProbe.cs|$probe_dir/VehicleReticleProbe.cs|" \
  planning/parity/vehicle-reticles/VehicleReticleProbe.tscn.txt > "$probe_dir/probe.tscn"
dotnet build VortexArena.csproj
echo "Captures: $capture_dir"
LIBGL_ALWAYS_SOFTWARE=1 LP_NUM_THREADS=2 VEHICLE_CAPTURE_DIR="$capture_dir" \
  xvfb-run -a "$GODOT" --path . --audio-driver Dummy --rendering-method gl_compatibility \
  --render-thread safe --resolution 800x600 "res://$probe_dir/probe.tscn"
```
