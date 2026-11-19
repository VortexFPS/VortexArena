# S1 row 13 casing impact audio and lifetime

The reference was the clean Xonotic checkout at `863cd3e8456e18f577f7fe5fef857ff55ae62cc0`. The stock SDL client used the provided byte-identical QC and SDL libraries. The native capture used Godot 4.6.3 Mono Compatibility with a .NET 10 runtime rolling forward the net8 build. Both ran in graphical Xvfb windows with disposable profiles.

The stock run fired the Shotgun and MachineGun from a fixed Stormkeep spawn and facing. It captured the PulseAudio sink in four trials with `g_casings` on and off. The native run ejected both models through `EffectSystem.Spawn` onto a controlled collision floor under a fixed camera and recorded the Master bus. Its casing touch used the real `ClientWorld` sound hook and default DarkPlaces mixer. The cameras and collision layouts serve to isolate the events in each engine. The captures compare sample families and lifetime behavior without assuming equal sound levels, positions or pixels across the two scenes. [Paired window captures](viewport.webp)

## Impact audio

The pinned touch handler deletes on NOIMPACT, checks for a world or BSP touch over speed 50, and uses `CH_SHOTS -4`, `VOL_BASE 0.7`, and `ATTEN_LARGE 1`. The native hook already sends those parameters. The tested `.wav` requests resolved to the shipped Ogg data for both casing types. No sample, volume, attenuation or channel edit was needed.

The strongest stock shell-family waveform match rose from 0.252 with casings off to 0.730 with them on. The brass family rose from 0.209 to 0.741. The native mixer logged its sample requests, and the Master recording matched those same sample families. The coefficients measure short waveform similarity after first-differencing. The four audio excerpts run in order from stock shell to native shell to stock brass to native brass. Stock excerpts include other sounds from the live window. [Audio excerpts](impact-excerpts.wav) · [Sample matches and mixer events](evidence.txt)

Stock evaluates the 0.2-second cooldown at client draw time. Catch-up tics run within that same draw. Native previously used each simulated tic's timestamp to rearm the cooldown, allowing an extra impact sound within a catch-up draw. It now compares and rearms at draw time. A focused catch-up fixture reached two separated contacts within 22 sweeps. With the old throttle it emitted two sounds. With the draw-time throttle it emitted one. Moving-brush touch and a spatial gain sweep were outside these runs. Stock selects the casing message’s silent bit using its self-recipient sound rule, which allows these impacts.

## Lifetime and artwork

Both configs ship `cl_casings_shell_time 30`, `cl_casings_bronze_time 10`, `cl_casings_ticrate 0.03125`, `cl_casings_sloppy 1` and `cl_casings_maxcount 100`. Pinned `NET_HANDLE(casings)` reads the lifetime when it spawns each casing. Pinned `Casing_Draw` clamps remaining seconds to alpha in 0–1, then removes the object below 0.003. The stock brass edict read 0.4700 during its final second with a 5-second configured lifetime. The native baseline stayed visible beyond both shortened lifetime settings and had no instance alpha channel.

Native now reads the applicable lifetime at spawn and fades in place during the last second. The probe retained separate 3- and 4-second lifetimes, measured 0.5 instance alpha at the midpoint for both loaded meshes, and checked that a live cvar edit only affects subsequent spawns. Actual screen contrast against the empty floor at half opacity was about half the opaque contrast. Original and Modern particle modes both passed. The native run also exercised the default DarkPlaces colour rules and the alternate linear-light mode. The existing 42 motion, cap and viewport checks passed with the new batches. [Viewport measurements](evidence.txt)

ADR-0001 permits a native renderer implementation. ADR-0006 governs the asset pipeline, and ADR-0019 keeps stock QC execution in the legacy stack. Original, Mixed and Modern are the particle router's settings. Casing meshes bypass that router in `EffectSystem`. The opacity change shares the existing models, textures and material inputs. Two additional fixed fade buffers preserve the opaque depth path and keep the documented 1,024-live-casing ceiling across both types. The draw cost adds a batch per kind when casings are fading. Overlapping translucent casings use Godot’s batch sort order.

Top left in the contact sheet is the stock brass trial during its fade window. Top right is the native opaque reference. Bottom left is the native shell's half-opacity frame with brass still opaque. Bottom right is the brass half-opacity frame after the shell expired.

## Repeat the runs

The stock script needs PulseAudio, `parec`, Xvfb, xdotool and ImageMagick. It accepts `STOCK_DIR`, `XONOTIC_PIN` and `CASING_PROBE_PORT` overrides.

```sh
xvfb-run -a -s '-screen 0 1000x750x24' bash planning/parity/rubble-limits/casing-impact/reproduce-stock.sh
```

The native driver stages an already restored worktree, opens a fixed-camera viewport, and writes its captures and log to the printed directory. Set `CASING_PARTICLES=2 CASING_COLOUR=0` to repeat the second presentation configuration.

```sh
GODOT=/path/to/Godot_v4.6.3-stable_mono_linux.x86_64 DOTNET_ROLL_FORWARD=Major \
  bash planning/parity/rubble-limits/casing-impact/reproduce-native.sh
```
