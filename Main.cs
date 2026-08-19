using System;
using System.Runtime.InteropServices;
using Godot;
using VortexArena.Common.Diagnostics;
using VortexArena.Common.Gameplay;
using VortexArena.Game;
using VortexArena.Game.Net;

namespace VortexArena;

/// <summary>
/// Boot scene for the VortexArena Godot host. Prints the discovered registries (proving the C# libraries
/// load) and then launches the walkable <see cref="GameDemo"/> — which boots the gameplay/engine facade
/// and drives the ported player movement. This will grow into the real client/server bootstrap
/// (see planning/legacy/todo/phase-2..3).
/// </summary>
public partial class Main : Node
{
    public override void _Ready()
    {
        // C1: bias the GC toward low frame-pause latency for the interactive client — SustainedLowLatency defers
        // BLOCKING Gen2 collections (deferring the long pauses, not all collection), the right mode for a render
        // loop. Gated to the windowed client: a headless/dedicated host keeps the default latency mode (a
        // long-running server favours bounded memory + throughput over per-frame pauses). The GC FLAVOR
        // (workstation + concurrent) is pinned in VortexArena.csproj. Set first so it spans the whole session.
        if (DisplayServer.GetName() != "headless")
            System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;

        // Route the C# log facade (lib/log.qh successor, VortexArena.Common.Diagnostics.Log) to Godot's console so
        // every LOG_INFO/WARN/TRACE/DEBUG/SEVERE/FATAL is visible in the editor Output panel + the player
        // console — the verbosity gate is the `developer` cvar (set developer 1 → trace, 2 → debug). Wired
        // first so even boot-time logging lands. Faults (Fatal/Severe) go through GD.PushError so they also
        // register in the editor's Debugger → Errors dock; the rest render with GD.PrintRich, colour-tinted by
        // level (and any Quake `^` colour codes translated to BBCode) via Log.ToBBCode. Single Godot process =
        // single tag; "client" (a listen server shares this process — a future dedicated server would set
        // "server").
        Log.Program = "client";
        Log.Sink = static (level, line) =>
        {
            if (level is LogLevel.Fatal or LogLevel.Severe)
                GD.PushError(Log.StripColors(line));       // real fault → Errors dock + stderr/Output
            else
                GD.PrintRich(Log.ToBBCode(level, line));   // colour-tinted, leveled output in the Output panel
        };

        // Build the weapon/item/mutator/gametype catalogs by scanning the loaded assemblies
        // (reflection bootstrap; the source generator will replace this at compile time — ADR-0003).
        GameRegistries.Bootstrap();

        // Register the dynamic map/scene colour-tint global shader parameters (VortexArena.Game.WorldTint).
        // MUST happen before any world/skin shader that declares them compiles (i.e. before the first map loads),
        // else Godot rejects the unknown global and the surface fails to render — so it's done here, up front.
        WorldTint.EnsureRegistered();

        // Always-on frame-time + GC hitch monitor (session-wide: menu + match). Self-driving; the on-screen
        // graph + hitch logging are gated by cl_frameprofiler (debug-default-on). Added early so it spans boot.
        AddChild(new VortexArena.Game.Client.FrameProfiler { Name = "FrameProfiler" });

        // Boot banner through the new facade (a live end-to-end exercise of it): the summary at LOG_INFO
        // (always shown), the per-registry dump at LOG_TRACE — uncluttered at `developer 0`, and visible the
        // moment you `set developer 1`. Run with `developer 2` to also get DEBUG + source locations.
        Log.Info("=== VortexArena boot ===");
        Log.Info($"Weapons:   {Weapons.All.Count}");
        Log.Info($"Items:     {Items.All.Count}");
        Log.Info($"Mutators:  {Mutators.All.Count}");
        Log.Info($"GameTypes: {GameTypes.All.Count}");
        Log.Info($"Sounds:    {VortexArena.Common.Gameplay.Sounds.All.Count}");
        foreach (var w in Weapons.All)
            Log.Trace($"  weapon[{w.RegistryId}] {w.RegistryName} — {w.DisplayName}");

        string[] args = OS.GetCmdlineArgs();

        // `--bake-sdf <map.bsp> [-o out.psdf]` (planning/particles-dual-system.md §A.7): the headless SDF
        // compiler-side baker — reuses the §A.3 generator verbatim (zero drift vs the load-time output), writes
        // maps/<map>.psdf for the pk3, then quits. Runs before any game/menu boot.
        if (Array.IndexOf(args, VortexArena.Engine.Particles.SdfBakeCli.Flag) >= 0)
        {
            int code = VortexArena.Engine.Particles.SdfBakeCli.Run(args);
            GetTree().Quit(code);
            return;
        }

        // `--net-loopback` runs the whole networked stack in-process (server+bot+client+render) — an
        // end-to-end exercise of the §2 netcode (auth handshake, delta snapshots, entity rendering, radar).
        if (Array.IndexOf(args, "--net-loopback") >= 0)
        {
            AddChild(new NetLoopback { Name = "NetLoopback" });
        }
        else
        {
            // The application shell: boots into the main menu front-end and owns the menu↔match lifecycle.
            // `--map <vpath>` boots straight into a no-net local match on that map, bypassing the menu (CI/dev/smoke).
            // `--model <name>` boots the no-net player-model viewer (turntable of models/player/<name>.iqm) for
            //   windowed visual-QA capture; pair with `--screenshot` (tools/visual-qa.sh drives the model sweep).
            // `--connect <addr>` joins a REAL networked server (host[:port], default 26000) — the full predicted
            //   client + render + camera + HUD (NetGame, client mode).
            // `--host [map]` boots a listen server (ServerNet on a GameWorld) and self-connects a networked client;
            //   pair with `--map`/`--gametype`/`--bots N` to pick the map / gametype / bot count.
            // `--gametype <short>` (e.g. dm/ctf/rc) selects the boot gametype — drives the per-gametype
            //   map-entity filter (which conditional walls appear); defaults to "dm".
            var shell = new Shell { Name = "Shell" };
            // `--data <dir>` overrides the asset root (default res://data). Mainly an escape hatch for
            // a packaged build whose data dir isn't beside the binary (ADR-0014: the exported default already
            // resolves exe-relative, so this is rarely needed) and for pointing a dev build at an external
            // gamedir. An absolute/user:// path here bypasses the res:// exe-relative resolution entirely.
            int d = Array.IndexOf(args, "--data");
            if (d >= 0 && d + 1 < args.Length)
                shell.DataPath = args[d + 1];
            int m = Array.IndexOf(args, "--map");
            if (m >= 0 && m + 1 < args.Length)
                shell.BootMap = args[m + 1];
            // `--model <name>` boots the no-net player-model viewer (visual-QA capture of one hero model from
            // several angles), parallel to `--map`. A bare name resolves to models/player/<name>.iqm.
            int mdl = Array.IndexOf(args, "--model");
            if (mdl >= 0 && mdl + 1 < args.Length)
                shell.BootModel = args[mdl + 1];
            int gt = Array.IndexOf(args, "--gametype");
            if (gt >= 0 && gt + 1 < args.Length)
                shell.BootGametype = args[gt + 1];
            int ms = Array.IndexOf(args, "--menu-screen");
            if (ms >= 0 && ms + 1 < args.Length)
                shell.DebugScreen = args[ms + 1];

            // `--camera-trace <scenario.json> <out.json>` (apparatus A2): boot the listen server on the scenario's
            // map, feed NetGame the scripted per-tick input, and dump the per-frame rendered camera + predicted
            // origin to out.json, then quit. Run with `--headless --fixed-fps 72` for a deterministic capture. The
            // scenario sets the map/gametype; we force a 0-bot listen server so nothing else perturbs the player.
            int ct = Array.IndexOf(args, "--camera-trace");
            if (ct >= 0 && ct + 2 < args.Length && CameraTrace.Configure(args[ct + 1], args[ct + 2]))
            {
                shell.BootMap = CameraTrace.Map;
                shell.BootGametype = CameraTrace.Gametype;
                shell.BootBots = CameraTrace.Bots; // 0 by default; a scenario can request bots to capture the bot-join transition
            }

            // `--observe "<x y z> [yaw pitch]"` [+ `--look-at "<x y z>"`]: dev/CI free camera — keep the local
            // client an OBSERVER (no auto-join, no body/viewmodel) and pin the camera at that Quake-space point,
            // aimed by yaw/pitch or at the --look-at target. Pair with `--map <name> --screenshot <path>` to
            // frame any spot on a map (an item pickup, a lightmap seam) without scripting player movement.
            int ob = Array.IndexOf(args, "--observe");
            if (ob >= 0 && ob + 1 < args.Length)
            {
                int la = Array.IndexOf(args, "--look-at");
                ObserverCamera.Configure(args[ob + 1], la >= 0 && la + 1 < args.Length ? args[la + 1] : null);
            }

            // Networked boot paths (CI/dev): join a server, or host a listen server + self-connect.
            int c = Array.IndexOf(args, "--connect");
            if (c >= 0 && c + 1 < args.Length)
                shell.ConnectAddress = args[c + 1];
            if (Array.IndexOf(args, "--host") >= 0)
            {
                shell.BootHost = true;
                // `--host <map>` (an arg right after the flag that isn't another flag) is a convenient map shorthand.
                int h = Array.IndexOf(args, "--host");
                if (h + 1 < args.Length && !args[h + 1].StartsWith("--") && string.IsNullOrEmpty(shell.BootMap))
                    shell.BootMap = args[h + 1];
            }
            // DS-1 `--dedicated [map]` (DP `-dedicated`): boot a CLIENT-LESS server — same host path as --host,
            // but NetGame builds no local client (see NetGame._dedicatedNoClient), so no player slot is burned
            // and the browser's player count is real. The dedicated-server export template implies it via
            // OS.HasFeature("dedicated_server"). `--headless --host` intentionally keeps the v1 observer host.
            if (VortexArena.Game.Net.NetGame.DedicatedRequested)
            {
                shell.BootHost = true;
                int ded = Array.IndexOf(args, "--dedicated");
                if (ded >= 0 && ded + 1 < args.Length && !args[ded + 1].StartsWith("--")
                    && string.IsNullOrEmpty(shell.BootMap))
                    shell.BootMap = args[ded + 1];
            }
            int b = Array.IndexOf(args, "--bots");
            if (b >= 0 && b + 1 < args.Length && int.TryParse(args[b + 1], out int bots))
                shell.BootBots = bots;
            // `--port <n>` (DP `-port`): bind the hosted listen server off the stock 26000 — scripted/agent
            // runs must not collide with a live instance (a busy port makes the host's self-client attach to
            // the WRONG server behind a plausible-looking handshake; see RUNNING.md).
            int pt = Array.IndexOf(args, "--port");
            if (pt >= 0)
            {
                // Parsed and range-checked by VortexArena.Net.HostPort, which is also what
                // Shell.ResolveHostPort probes through. The range check is not tidiness: IPEndPoint THROWS on
                // anything outside 1..65535 rather than reporting it busy, so an unchecked value would take
                // the host path down instead of falling back. A bad --port logs and boots on the default
                // rather than refusing to start — a mistyped flag should not block the menu.
                string? portText = pt + 1 < args.Length ? args[pt + 1] : null;
                if (VortexArena.Net.HostPort.TryParse(portText, out int port, out string? portError))
                {
                    shell.BootPort = port;
                    shell.BootPortExplicit = true; // pinned: host FAILS if taken, no auto-increment (Shell.ResolveHostPort)
                }
                else
                {
                    Log.Severe($"[Main] --port: {portError}. Hosting on {shell.BootPort} instead.");
                }
            }
#if VA_BOTPLAYER
            // `--bot-player [skill]`: hand the LOCAL player to a bot brain so an unattended run drives the real
            // player pipeline (see Directory.Build.props / VaBotPlayer). The flag only exists in a build that
            // opted in at COMPILE time — a normal binary has no way to reach this, by construction.
            int bp = Array.IndexOf(args, "--bot-player");
            if (bp >= 0)
            {
                shell.BootBotPlayer = true;
                if (bp + 1 < args.Length && !args[bp + 1].StartsWith("--")
                    && float.TryParse(args[bp + 1], System.Globalization.NumberStyles.Float,
                                      System.Globalization.CultureInfo.InvariantCulture, out float bpSkill))
                    shell.BootBotPlayerSkill = bpSkill;
            }
#endif
            AddChild(shell);
        }

        // Optional dev/CI visual capture: `--screenshot <path> [--screenshot-frames N]` lets the scene settle,
        // saves the rendered viewport to a PNG, and quits. Windowed only (headless renders blank — see
        // docs/RUNNING.md "Visual capture"). An agent can then read the PNG to *see* the running game.
        MaybeCaptureScreenshot(args);

        // `--quit-after-seconds <s>`: wall-clock self-quit for scripted/CI runs (the headless host smoke).
        // Godot's own `--quit-after` counts FRAMES (wall-time varies wildly headless), and Windows `timeout`
        // can't kill the Godot child — an orphaned host then holds UDP 26000 and later runs fail with
        // "Couldn't create an ENet host". ProcessAlways (the default) so a paused tree still quits.
        int q = Array.IndexOf(args, "--quit-after-seconds");
        if (q >= 0 && q + 1 < args.Length && double.TryParse(args[q + 1],
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out double quitSecs) && quitSecs > 0)
        {
            GetTree().CreateTimer(quitSecs).Timeout += () => GetTree().Quit();
        }

        // DS-4: graceful shutdown on SIGTERM/SIGINT (systemd `stop`, Ctrl+C, a supervisor stop). Without this a
        // signal HARD-kills the process mid-frame — the ENet host never closes (peers hang until they time out)
        // and the UDP port isn't released (the next host then fails to bind, the RUNNING.md orphaned-port trap).
        // We cancel the default terminate and instead post a clean quit to the MAIN thread (the handler runs on a
        // threadpool thread, so every Godot touch must be deferred): notify connected clients, then Quit(0) —
        // which runs the normal teardown (NetGame._ExitTree → Shutdown → transport Close, releasing the port, and
        // the config-save guard). On Windows SIGINT is Ctrl+C; SIGTERM is best-effort (the CLR catches what the OS
        // delivers). Registrations are stored so the GC can't collect them (that would silently drop the handler).
        RegisterShutdownSignals();
    }

    // Kept alive for the process lifetime — a collected PosixSignalRegistration removes its handler.
    private PosixSignalRegistration? _sigTerm, _sigInt;
    private int _shuttingDown; // 0/1 latch so a repeated signal (impatient Ctrl+C) doesn't re-enter the quit

    private void RegisterShutdownSignals()
    {
        void Handle(PosixSignalContext ctx)
        {
            // ESCALATION PATH: only the FIRST signal is cancelled+handled gracefully. A second one is left
            // uncancelled so the runtime terminates the process itself. Cancelling unconditionally meant that
            // once the latch was set — including when the graceful quit could not run, e.g. the main loop
            // blocked in a synchronous BSP/collision build, or GracefulQuit threw because the node had left
            // the tree — every later SIGTERM/SIGINT was swallowed. The server then ignored `systemctl stop`
            // until TimeoutStopSec expired, and Ctrl+C forever, leaving only kill -9.
            if (System.Threading.Interlocked.Exchange(ref _shuttingDown, 1) != 0)
            {
                GD.Print($"[Main] received {ctx.Signal} again; letting the runtime terminate.");
                return; // ctx.Cancel stays false → the OS default action runs
            }
            ctx.Cancel = true; // first signal: quit gracefully below
            GD.Print($"[Main] received {ctx.Signal}; shutting down gracefully (signal again to force).");
            Callable.From(GracefulQuit).CallDeferred();
        }

        try { _sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Handle); } catch { /* unsupported on this OS */ }
        try { _sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Handle); } catch { /* unsupported on this OS */ }
    }

    /// <summary>Runs on the MAIN thread (posted via CallDeferred): tell any live server its clients are about to
    /// lose the link, then quit cleanly with success. The normal tree teardown does the ENet close + port release.</summary>
    private void GracefulQuit()
    {
        try { NetGame.GracefulShutdownHook?.Invoke(); } catch { /* best-effort client notice */ }
        // Null-guarded like the sibling DS-4 quit sites: this runs deferred, so the node may have left the
        // tree in between. Throwing here would leave the shutdown latch set with the process still alive and
        // (before the escalation path above) deaf to every further signal.
        GetTree()?.Quit(0);
    }

    /// <summary>
    /// If launched with <c>--screenshot &lt;path&gt;</c>, attach a <see cref="ScreenshotHook"/> that waits a few
    /// rendered frames (override the count with <c>--screenshot-frames N</c>, default 90), writes the viewport
    /// to that PNG, and quits. No-op without the flag. Run WINDOWED — <c>--headless</c> captures blank.
    /// </summary>
    private void MaybeCaptureScreenshot(string[] args)
    {
        int i = Array.IndexOf(args, "--screenshot");
        if (i < 0 || i + 1 >= args.Length)
            return;

        int frames = 90;
        int f = Array.IndexOf(args, "--screenshot-frames");
        if (f >= 0 && f + 1 < args.Length && int.TryParse(args[f + 1], out int n) && n >= 0)
            frames = n;

        AddChild(new ScreenshotHook { Name = "ScreenshotHook", OutPath = args[i + 1], WarmupFrames = frames });
    }
}
