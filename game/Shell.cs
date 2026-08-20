using System.Linq;
using Godot;
using VortexArena.Common.Diagnostics;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Game.Console;
using VortexArena.Game.Menu;
using VortexArena.Server;

namespace VortexArena.Game;

/// <summary>
/// The application shell — the C# successor to the engine's menu/client lifecycle (DP <c>menu_restart</c> +
/// the <c>CL_</c> connect/disconnect flow). It owns the single front-end <see cref="MenuRoot"/> and the live
/// match (<see cref="VortexArena.Game.Net.NetGame"/> — a listen server or a remote client), and switches
/// between them: boot into the main menu, start a match from the Create/Singleplayer screens, drop the in-game
/// menu on Escape (Xonotic's behavior), and tear the match down on Disconnect. It also performs the one-time
/// client bootstrap (<see cref="MenuState.Boot"/>),
/// applies the saved settings to the engine, and wires the <see cref="MenuCommand"/> host hooks + the menu's
/// StartGame/Connect callbacks to real actions.
///
/// Pause model (#19 auto-pause): <see cref="SyncAutoPause"/> sets <c>GetTree().Paused</c> for a SOLO local game
/// while the pause menu / console is open or the window is unfocused. The shell and its menu layer are
/// <see cref="Node.ProcessModeEnum.Always"/> so they keep running (Escape, the menu UI, the pause release).
/// NOTE: <see cref="VortexArena.Game.Net.NetGame"/> is ALSO deliberately Always — pausing its subtree would
/// starve the ENet pump and time the link out — so the tree pause does NOT stop its _Process; instead NetGame
/// freezes the authoritative sim itself by driving the server tick with dt=0 while <c>GetTree().Paused</c>
/// (transport keeps pumping, zero fixed ticks run). The ModelViewer (no netcode) is plain Pausable.
/// </summary>
public partial class Shell : Node
{
    /// <summary>The game data dir to mount (forwarded to <see cref="MenuState.Boot"/> + each match).</summary>
    [Export] public string DataPath { get; set; } = "res://data";

    /// <summary>If set at boot, skip the menu and start straight into this map (CI/dev; the smoke test uses it).</summary>
    public string? BootMap { get; set; }

    /// <summary>If set at boot (CLI <c>--model &lt;name&gt;</c>), skip the menu and open the no-net player-model
    /// viewer on that hero model (windowed visual-QA capture; <c>tools/visual-qa.sh</c> drives the sweep).</summary>
    public string? BootModel { get; set; }

    /// <summary>The gametype short code to boot <see cref="BootMap"/> with (drives the per-gametype map-entity
    /// filter — which conditional walls appear). Defaults to Deathmatch.</summary>
    public string BootGametype { get; set; } = "dm";

    /// <summary>If set at boot, open this menu sub-screen for visual capture (dev/CI screenshot of one dialog).</summary>
    public string? DebugScreen { get; set; }

    /// <summary>If set at boot (CLI <c>--connect &lt;addr&gt;</c>), skip the menu and join this server.</summary>
    public string? ConnectAddress { get; set; }

    /// <summary>If true at boot (CLI <c>--host [map]</c>), skip the menu and start a listen server on
    /// <see cref="BootMap"/> (a flat floor when empty) then self-connect a networked client.</summary>
    public bool BootHost { get; set; }

    /// <summary>Bot count for the <c>--host</c> listen server (CLI <c>--bots N</c>); 0 = no bots.</summary>
    public int BootBots { get; set; }

    /// <summary>Server slot count for this boot (CLI <c>--maxplayers N</c>, DP's <c>-dedicated N</c>/<c>-listen N</c>);
    /// 0 = unset, keep whatever the config tree chose. Applied in <see cref="_Ready"/> AFTER the cfg load rather
    /// than at parse time: DP lets <c>xonotic-server.cfg</c>'s own <c>maxplayers</c> win over its CLI flag, which
    /// would make this flag inert here, so it follows the port's <c>--cvar</c> convention (CLI is the last word)
    /// instead. Deliberate divergence — see docs/RUNNING.md.</summary>
    public int BootMaxPlayers { get; set; }

#if VA_BOTPLAYER
    /// <summary>Bot-player harness (CLI <c>--bot-player</c>): drive the LOCAL player from a bot brain so an
    /// unattended run exercises the real player pipeline. Compile-gated — see Directory.Build.props.</summary>
    public bool BootBotPlayer { get; set; }

    /// <summary>Skill for the bot-player brain (CLI <c>--bot-player &lt;skill&gt;</c>); default mid-range.</summary>
    public float BootBotPlayerSkill { get; set; } = 5f;
#endif

    /// <summary>UDP port every listen server this process hosts binds (CLI <c>--port N</c>, DP <c>-port</c>).
    /// Defaults to the stock game port; override it so scripted/agent runs don't collide with a live instance
    /// already holding 26000 (a second host on a busy port otherwise self-connects to the WRONG server).</summary>
    public int BootPort { get; set; } = VortexArena.Game.Net.NetGame.DefaultPort;

    /// <summary>True when the port was pinned by the user (CLI <c>--port N</c>). A pinned port is honoured
    /// exactly — hosting FAILS loudly if it is taken. An unpinned (default) port auto-increments through
    /// <see cref="HostPortScanLimit"/> instead, so a stray binder on 26000 (another live instance, a DarkPlaces
    /// client — both default there) can't silently swallow the listen server's self-connect.</summary>
    public bool BootPortExplicit { get; set; }

    /// <summary>Top of the auto-increment scan range for an unpinned host port (inclusive).</summary>
    private const int HostPortScanLimit = 26100;

    /// <summary>Boot-failure exit code for "UDP port in use", as documented in docs/RUNNING.md under DS-4.
    /// A supervisor (systemd <c>Restart=on-failure</c>, the launcher's RestartPolicy.OnFailure) reads this to
    /// tell "the port is taken, retrying will not help until something frees it" from a crash worth
    /// restarting.</summary>
    private const int HostPortInUseExit = 2;

    /// <summary>True when nothing interactive is in front of this process: a dedicated server, or any headless
    /// run (CI smokes, scripted captures, an agent driving the game).
    ///
    /// <para>The distinction that matters is "can a human read and dismiss a modal", not "is this a server" —
    /// <c>--headless --host</c> is a listen server with no display, and a dialog is exactly as useless there as
    /// on a dedicated box. Anything that would block waiting for a click has to take a different branch on
    /// this side of the check.</para></summary>
    private static bool IsUnattended =>
        VortexArena.Game.Net.NetGame.DedicatedRequested || DisplayServer.GetName() == "headless";

    private CanvasLayer _menuLayer = null!;
    private MenuRoot _menu = null!;
    private ModelViewer? _viewer;
    private VortexArena.Game.Net.NetGame? _netGame;
    private ConsoleOverlay _console = null!;
    private bool _paused;                    // the in-game (pause) menu is open
    private bool _windowFocused = true;      // OS window focus, tracked from _Notification (drives auto-pause)
    private CanvasLayer? _loadingLayer;
    private LoadingScreen? _loadingScreen;
    private Game.Hud.ChatPrompt _chatPrompt = null!;   // [#46] the messagemode chat input line

    /// <summary>[#46] <c>messagemode</c>/<c>messagemode2</c>: open the chat prompt (in a match only — at the
    /// menu there is nobody to talk to; print the standard hint instead, like other match-only commands).</summary>
    private void OpenChatPrompt(bool team)
    {
        if (!MatchRunning || ConsoleState.IsOpen)
        {
            if (!MatchRunning)
                VortexArena.Common.Diagnostics.Log.Help("messagemode: not connected — start a match first.");
            return;
        }
        _chatPrompt.Open(team);
    }

    /// <summary>
    /// DP <c>commandmode</c> (the <c>/</c> prompt): open the same input line, but the typed text is submitted as
    /// a raw COMMAND rather than chat, optionally prefilled. Base's interactive scoreboard uses it for Ctrl+T
    /// (<c>commandmode tell "&lt;player&gt;^7"</c>) so the player only has to type the message.
    /// </summary>
    private void OpenCommandPrompt(string prefill)
    {
        if (!MatchRunning || ConsoleState.IsOpen)
        {
            if (!MatchRunning)
                VortexArena.Common.Diagnostics.Log.Help("commandmode: not connected — start a match first.");
            return;
        }
        _chatPrompt.Open(team: false, commandMode: true, prefill: prefill ?? "");
    }

    /// <summary>Apply any <c>--cvar NAME VALUE</c> command-line overrides into the shared store (repeatable; each
    /// <c>--cvar</c> token consumes the next two args). For test/automation/A-B runs that need to pin a cvar at
    /// boot — e.g. <c>vid_vsync</c> / <c>cl_frameprofiler</c> — without touching a config file.</summary>
    private static void ApplyCvarOverrides()
    {
        string[] args = OS.GetCmdlineArgs();
        for (int i = 0; i + 2 < args.Length; i++)
        {
            if (args[i] != "--cvar")
                continue;
            MenuState.Cvars.Set(args[i + 1], args[i + 2]);
            VortexArena.Common.Diagnostics.Log.Info($"[shell] --cvar {args[i + 1]} = \"{args[i + 2]}\"");
            i += 2;
        }
    }

    /// <summary>
    /// DP's <c>+command</c> command line (<c>COM_AddCommandLine</c> / <c>Host_AddConfigText</c>): every argument
    /// beginning <c>+</c> is a console command, taking the arguments after it up to the next <c>+</c>/<c>--</c>
    /// flag — <c>vortex +toggleconsole</c>, <c>+exec mytest.cfg</c>, <c>+connect 10.0.0.4</c>. This is how a
    /// Xonotic player scripts a launch, and the port had no equivalent.
    ///
    /// <para>Run LAST in the boot sequence — after the config tree, the user config, the <c>--cvar</c> pins and
    /// the console's own command registrations — so a <c>+</c> command sees the fully-built console and is the
    /// final word, exactly as it is in DP. Deferred by one frame so a command that changes scenes
    /// (<c>+map</c>, <c>+connect</c>) does not re-enter the tree from inside <c>_Ready</c>.</para>
    /// </summary>
    private void RunBootCommands()
    {
        string[] args = OS.GetCmdlineArgs();
        var lines = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Length < 2 || args[i][0] != '+')
                continue;
            var parts = new List<string> { args[i][1..] };
            // Everything up to the next +command or --flag belongs to this command.
            while (i + 1 < args.Length && args[i + 1].Length > 0
                   && args[i + 1][0] != '+' && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                parts.Add(args[++i]);
            lines.Add(string.Join(' ', parts));
        }
        if (lines.Count == 0)
            return;

        Callable.From(() =>
        {
            foreach (string line in lines)
            {
                VortexArena.Common.Diagnostics.Log.Info($"[shell] +{line}");
                try { MenuState.Interp?.ExecuteLine(line); }
                catch (Exception ex)
                {
                    VortexArena.Common.Diagnostics.Log.Severe($"[shell] +{line} failed: {ex.Message}");
                }
            }
        }).CallDeferred();
    }

    /// <summary>DS-5: exec the operator's server.cfg on a server-host boot only (a pure client / model viewer never
    /// runs it). `--serverconfig &lt;name&gt;` overrides the filename (DP <c>-serverconfig</c>). The exec itself is a
    /// no-op when the file is absent, so this gate is really just "don't even look for it on a non-host boot".</summary>
    private void MaybeExecServerConfig()
    {
        bool hosting = BootHost || !string.IsNullOrWhiteSpace(BootMap);
        if (!hosting)
            return;
        string[] args = OS.GetCmdlineArgs();
        int sc = System.Array.IndexOf(args, "--serverconfig");
        string fileName = (sc >= 0 && sc + 1 < args.Length && !args[sc + 1].StartsWith("--"))
            ? args[sc + 1]
            : "server.cfg";
        MenuState.ExecServerConfig(fileName);
    }

    public override void _Ready()
    {
        // The shell (and, by inheritance, the menu layer) keeps processing while the tree is paused, so Escape
        // still toggles the in-game menu and the menu UI stays interactive over the frozen match.
        ProcessMode = ProcessModeEnum.Always;

        // --- one-time client bootstrap: mount assets, load the cvar config tree + user prefs, publish Api ---
        MenuState.Boot(DataPath);
        // DS-5: on a server-host boot (--host / --map / --dedicated), exec the operator's server.cfg AFTER the
        // stock tree + config.cfg (so operator settings override both) and BEFORE the --cvar pins below (so a CLI
        // override is still the last word). Absent by default (the install ships only server.cfg.example), so a
        // pure client / capture / smoke run that hasn't opted in is unaffected. `--serverconfig <name>` picks a
        // different file (DP -serverconfig).
        MaybeExecServerConfig();
        // `--maxplayers N`: last word on the server's slot count, applied after the stock tree + server.cfg have
        // both had their say (either may carry a `maxplayers` line) and before any server starts and adopts it.
        if (BootMaxPlayers > 0)
            VortexArena.Common.Config.ServerSlots.Set(BootMaxPlayers);
        // --cvar NAME VALUE (repeatable): pin a cvar at boot AFTER the config tree loads and BEFORE ApplyAll, so a
        // test/automation/A-B run can force e.g. `--cvar vid_vsync 2 --cvar cl_frameprofiler 2` without editing a
        // config. Overrides the loaded config.cfg value (last writer wins), exactly like a console `set` would.
        ApplyCvarOverrides();
        ClientSettings.ApplyAll();

        WireCommandHooks();

        // Let the 2D overlays resolve content textures from the SHARED (session-lifetime) asset loader. NetGame
        // used to be the only place that wired this, so anything drawn outside a match — the console's
        // gfx/conback background above all — had no way to load its art and silently fell back. NetGame still
        // re-points it at its own loader when a match starts; same content tree, so the swap is invisible.
        if (MenuState.SharedAssets is { } bootAssets)
            Game.Hud.TextureCache.VfsResolver ??= bootAssets.LoadTexture;

        // --- the engine screen overlay (DP SCR_DrawScreen's own readouts, chiefly `showfps`). Created HERE, once
        //     for the session, rather than inside the match HUD: DP never drew showfps from the QC HUD, so in
        //     Xonotic it is on screen at the menu and on the loading screen too. Hosting it in NetGame's Hud was
        //     why the port's showfps only worked inside a live match. ---
        AddChild(new Game.Hud.EngineOverlay { Name = "EngineOverlay" });

        // --- in-game developer console (backtick), on its own high CanvasLayer above the menu/HUD. Shares the
        //     boot command interpreter (MenuState.Interp) so typed lines interpret exactly like a .cfg, routes
        //     gameplay commands to the live world (in-process listen server) or the remote server, and recaptures
        //     the mouse on close only when a match is live and not paused. Created before any boot-into-match path
        //     below so an early `--host`/`--connect` already has a console. ---
        _console = new ConsoleOverlay { Name = "Console" };
        AddChild(_console);
        _console.Initialize(
            MenuState.Interp!,                       // non-null after MenuState.Boot
            MenuState.Cvars,
            LocalRouteCommand,                       // gameplay cmd → in-process listen-server world (null on a pure client)
            RouteRemoteCommand,                      // pure-client fallback: DP clc_stringcmd to the remote server (or print a hint at the menu)
            () => MatchRunning && !_paused);         // recapture the mouse on close only inside a live match

        // Keybind system: the runtime key→command table is already seeded by MenuState.Boot (above) from the
        // canonical binds-xonotic.cfg via the bind sink (BindInput.RegisterBindCommands), with the user's saved
        // `bind` lines layered on top — one source of truth shared by `bind`/gameplay input. Here we only wire
        // the runtime hook: release all held buttons whenever the console opens (DP in_releaseall).
        BindInput.Install();

        // [#46] The messagemode chat prompt (the DP engine chat input the stock ENTER/T/Y/Z binds target — Base
        // has NO QC-side chat input). Lives on its own high CanvasLayer (over the HUD, under the console) and
        // submits through the SAME dual route a console-typed `say` takes: the in-process listen world as a
        // client command, else the remote string-command channel. The commands exist even at the menu (they
        // print a hint), so a bind fired outside a match never lands in the void.
        var chatLayer = new CanvasLayer { Name = "ChatPromptLayer", Layer = 90 };
        AddChild(chatLayer);
        _chatPrompt = new Game.Hud.ChatPrompt { Name = "ChatPrompt" };
        _chatPrompt.Submit = line =>
        {
            if (LocalRouteCommand(line) is null)
                RouteRemoteCommand(line);
        };
        chatLayer.AddChild(_chatPrompt);
        MenuState.Interp!.RegisterCommand("messagemode", _ => OpenChatPrompt(team: false));
        MenuState.Interp!.RegisterCommand("messagemode2", _ => OpenChatPrompt(team: true));
        // DP commandmode [prefill…]: the raw-command variant of messagemode (see OpenCommandPrompt).
        MenuState.Interp!.RegisterCommand("commandmode",
            a => OpenCommandPrompt(a.Count >= 2 ? string.Join(' ', a.Skip(1)) : ""));
        // Direct hook for callers that must preserve exact quoting in the prefill (the scoreboard's Ctrl+T tell).
        Menu.MenuCommand.OpenCommandPrompt = OpenCommandPrompt;

        // The client-side `screenshot` command (DP CF_CLIENT, bound to F12 by binds-xonotic.cfg): a Godot node that
        // grabs the next rendered frame and writes it to user://screenshots/. Registered on the SHARED interpreter
        // so both the F12 bind (NetGame.RunBoundCommand → RunCommand → ExecuteLine) and a console-typed `screenshot`
        // reach it — and, being a registered command, it's caught client-side before the server router (never
        // forwarded, like DP). The cvar defaults were seeded above by ClientSettings.ApplyAll.
        var screenshots = new Client.ScreenshotService { Name = "ScreenshotService" };
        AddChild(screenshots);
        screenshots.RegisterCommand(MenuState.Interp!, MenuState.Cvars);

        // Editable-map-format commands (vmap_import/_info/_list): local authoring actions, so they register
        // client-side on the shared interpreter and never route to the server. The asset system is passed so a
        // .map import can resolve real texture sizes for its texel-based texdefs.
        if (MenuState.Vfs is { } vfs)
        {
            var vmaps = new Vmap.VmapService(vfs, MenuState.SharedAssets?.Assets);
            vmaps.RegisterCommands(MenuState.Interp!);
        }

        // `r_missingtextures [map] [-v]` — which of a map's textures will not render. Client-side and local like
        // the vmap commands: it audits this install's search path, so it never routes to the server. The no-arg
        // form needs the map the client is in, which is resolved per invocation (no match exists yet at boot).
        if (MenuState.SharedAssets is { } sharedAssets)
            Loaders.MissingTextures.RegisterCommand(
                MenuState.Interp!, sharedAssets, () => _netGame?.CurrentMap ?? string.Empty);

        // r_restart / vid_restart need the same answer: which map is the CLIENT in? Same resolver, because
        // the "mapname" cvar lives on the listen server's private store and never reaches the console's.
        VortexArena.Game.Console.ConsoleOverlay.CurrentMapResolver = () => _netGame?.CurrentMap ?? string.Empty;

        // `r_vram_census [N]` — inventory the RESIDENT texture cache by category + top offenders (perf
        // 2026-08-02: names where the ~3.4 GB actually lives; gl_texturecompression measured ~0 delta, so the
        // bulk is outside the compressible classes). Prefers the live match's cache (the resident set),
        // falling back to the shared menu instance before a match exists.
        MenuState.Interp!.RegisterCommand("r_vram_census", args =>
        {
            Loaders.AssetSystem? sys = _netGame?.LiveAssets ?? MenuState.SharedAssets?.Assets;
            if (sys is null) { Log.Info("[vram census] no asset system yet"); return; }
            int top = args.Count > 0 && int.TryParse(args[0], out int n) ? n : 25;
            foreach (string line in sys.VramCensus(top).TrimEnd().Split('\n'))
                Log.Info(line);
        });
        // ...and the same census, once, in every profiler session summary — so a capture's "vram NMB" monitor
        // line names its own composition without anyone having to remember the console command.
        Client.FrameProfiler.VramCensusProvider =
            () => (_netGame?.LiveAssets ?? MenuState.SharedAssets?.Assets)?.VramCensus(10) ?? "(no asset system)";

        // Editor view aids: the world-space alignment grid's cvars + its `editor_grid` / `editor_grid_size`
        // commands. Client-side and bindable; the grid node itself lives in the match scene (NetGame).
        Vmap.EditorGrid.RegisterDefaults(MenuState.Cvars);
        Vmap.EditorGrid.RegisterCommands(MenuState.Interp!, MenuState.Cvars);
        Vmap.EditorController.RegisterDefaults(MenuState.Cvars);
        Vmap.EditorOrthoView.RegisterDefaults(MenuState.Cvars);

        // Dev/CI: `--menu-screen nexposee:<Title>` opens that panel inside the nexposee on boot (vs the plain
        // `--menu-screen settings` which pushes a framed dialog). Consumed by MainMenu; clear it so the
        // OpenDebugScreen path below doesn't also try to push an unknown screen.
        if (DebugScreen is { } ds && ds.StartsWith("nexposee:"))
        {
            MainMenu.AutoOpen = ds["nexposee:".Length..];
            DebugScreen = null;
        }

        // --- the front-end menu, on a CanvasLayer above any 3D match ---
        _menuLayer = new CanvasLayer { Name = "MenuLayer", Layer = 10 };
        AddChild(_menuLayer);
        _menu = new MenuRoot();
        _menu.ResumeRequested += Resume;
        _menu.DisconnectRequested += ReturnToMainMenu;
        _menuLayer.AddChild(_menu); // MenuRoot._Ready shows the main menu

        MouseCapture.SetWantCapture(false); // at the menu the cursor is free

        // Optional: boot straight into a match (smoke test / dev), bypassing the menu.
#if VA_BOTPLAYER
        // Bot-player harness: latch the request before any match starts, so NetGame binds the brain as soon
        // as the local player exists. Compile-gated — see Directory.Build.props.
        VortexArena.Game.Net.BotPlayerMode.Requested = BootBotPlayer;
        VortexArena.Game.Net.BotPlayerMode.Skill = BootBotPlayerSkill;
        if (BootBotPlayer)
            GD.Print("[bot-player] --bot-player: the local player will be driven by a bot brain.");
#endif

        if (!string.IsNullOrWhiteSpace(ConnectAddress))
            ConnectToServer(ConnectAddress!);               // --connect <addr>: join a real server
        else if (BootHost)
            StartListenServer(new MatchConfig { Map = BootMap ?? "", Gametype = BootGametype, BotCount = BootBots }); // --host [map]
        else if (!string.IsNullOrWhiteSpace(BootMap))
            // --map: a quick local match on the chosen map — a 0-bot listen server (the SAME bring-up Create
            // Game / --host use, just unattended + bot-free). The old no-net GameDemo path is gone; one entry
            // path now, so a test/capture run exercises exactly what real play does.
            StartListenServer(new MatchConfig { Map = BootMap!, Gametype = BootGametype, BotCount = 0 });
        else if (!string.IsNullOrWhiteSpace(BootModel))
            StartModelViewer(BootModel!);                   // --model: no-net player-model viewer (visual QA)
        else if (!string.IsNullOrWhiteSpace(DebugScreen))
            OpenDebugScreen(DebugScreen!);
        else
        {
            // Plain menu boot (the real launch path): warm the map-independent eager asset set into the shared
            // cache in the background NOW, so the first match's precache is a cache hit and the map loads fast.
            // Skipped above for a direct --map/--host/--connect boot — that match runs its own precache.
            StartMenuAssetWarm();
            // …and, on this path only, the development-release disclaimer over the main menu. Deliberately NOT
            // on the boot-into-match / --menu-screen branches: automation and CI must never have a modal to
            // dismiss (see MaybeShowStartupDisclaimer).
            MaybeShowStartupDisclaimer();
        }

        // DP's `+command` command line, last: after every config, pin and registration above, so a launch-time
        // console command is the final word (and can act on the boot path chosen just now).
        RunBootCommands();
    }

    /// <summary>
    /// Push the development-release disclaimer (<see cref="Menu.DialogDisclaimer"/>) over the freshly-shown main
    /// menu, unless the player has turned it off. Gated on <c>cl_startup_disclaimer</c> (default 1); the dialog's
    /// "Don't show this again" checkbox writes 0 and its OK button persists that to config.cfg, so the next
    /// launch goes straight to the menu. `set cl_startup_disclaimer 1` in the console brings it back.
    /// </summary>
    private void MaybeShowStartupDisclaimer()
    {
        if (MenuState.Cvars.GetFloat("cl_startup_disclaimer") == 0f)
            return;
        _menu.Push(new DialogDisclaimer());
    }

    /// <summary>
    /// Phase 2 of the loading-speed work: at a plain menu boot, warm the map-independent eager asset set (weapons,
    /// stock player models, combat sounds) into MenuState's process-lifetime <see cref="MenuState.SharedAssets"/>
    /// in the background (<see cref="Client.MenuAssetWarmer"/>), so the first match's precache collapses to cache
    /// hits and the map loads fast. Gated on <c>cl_persist_asset_cache</c> (the warm is wasted if the match builds
    /// its own loader) and <c>cl_warm_at_boot</c>, and skipped without a mounted data dir (no shared loader).
    /// </summary>
    private void StartMenuAssetWarm()
    {
        if (MenuState.SharedAssets is null)
            return;
        if (MenuState.Cvars.GetFloat("cl_persist_asset_cache") == 0f
            || MenuState.Cvars.GetFloat("cl_warm_at_boot") == 0f)
            return;
        string localModel = MenuState.Cvars.GetString("_cl_playermodel");
        var warmer = new Client.MenuAssetWarmer(MenuState.SharedAssets, localModel)
        {
            Name = "MenuAssetWarmer",
            ProcessMode = ProcessModeEnum.Always,   // keep warming even if a match starts + pauses the tree mid-warm
        };
        AddChild(warmer);
    }

    /// <summary>Dev/CI: push a named sub-screen so a screenshot can capture that one dialog.</summary>
    private void OpenDebugScreen(string id)
    {
        // "settings" or "settings:TabName" (e.g. settings:Audio) to capture a specific tab.
        string baseId = id.Contains(':') ? id[..id.IndexOf(':')] : id;
        string? tab = id.Contains(':') ? id[(id.IndexOf(':') + 1)..] : null;
        Control? screen = baseId.ToLowerInvariant() switch
        {
            "settings" => new DialogSettings(),
            "media" => new MediaScreen(),
            "multiplayer" => new MultiplayerScreen(),
            "singleplayer" => new SingleplayerScreen(),
            "create" => new CreateGameScreen(),
            "credits" => new CreditsScreen(),
            "quit" => new QuitDialog(),
            "pause" => new PauseMenu(),
            "profile" => new DialogMultiplayerProfile(),
            "mutators" => new DialogMutators(),
            "serverinfo" => new DialogServerInfo(),
            // Reachable only by clicking a Xonotic row in the browser, so it needs an id of its own to be
            // inspectable in a capture at all.
            "incompatible" => new DialogIncompatibleServer("^3Example ^7Xonotic Server", "192.0.2.10:26000"),
            // Reached at runtime only on a failed connect (reject/drop/timeout), so it needs its own id to inspect.
            "connfailed" => new DialogConnectionFailed("192.0.2.10:26000", "Couldn't connect to the server."),
            "teamselect" => new DialogTeamSelect(),
            "firstrun" => new DialogFirstRun(),
            "tos" => new DialogTermsOfService(),
            "welcome" => new DialogWelcome(),
            "hudpanels" => new DialogHudPanels(),
            "hudweapons" => new DialogHudPanelWeapons(),
            "cvarlist" => new DialogCvarList(),
            "disclaimer" => new DialogDisclaimer(),
            "sandbox" => new DialogSandboxTools(),
            _ => null,
        };
        if (screen is not null)
        {
            _menu.Push(screen);
            if (tab is not null && screen is DialogSettings ds)
                ds.SelectTab(tab);
            else if (tab is not null && screen is MediaScreen mediaScreen)
                mediaScreen.SelectTab(tab);
            else if (tab is not null && screen is MultiplayerScreen mp)
                mp.SelectTab(tab);
        }
        else
        {
            Log.Warn($"[Shell] unknown --menu-screen '{id}'.");
        }
    }

    /// <summary>Point the menu's StartGame/Connect callbacks and the command dispatcher at real shell actions.</summary>
    private void WireCommandHooks()
    {
        CreateGameScreen.StartGameRequested += OnStartGame;
        MultiplayerScreen.Browser.ConnectRequested += OnConnect;

        MenuCommand.Quit = () => GetTree().Quit();
        MenuCommand.Disconnect = ReturnToMainMenu;
        MenuCommand.ToggleMenu = HandleToggleMenu;
        MenuCommand.VideoRestart = ClientSettings.ApplyVideo;
        MenuCommand.AudioRestart = ClientSettings.ApplyAudio;
        // DP parity (2026-07-11): `vid_vsync 1` / `cl_maxfps 144` typed in the console take effect INSTANTLY,
        // like DP — no vid_restart needed for the non-window-mode video cvars. Wired after boot-config load so
        // the bulk cfg exec doesn't re-apply per line.
        ClientSettings.InstallLiveVideoCvars(MenuState.Cvars);
        // QC `map`/`devmap`: in a running match this is a changelevel (keep mode + bots); at the menu it starts a
        // fresh listen server on the map then self-connects (the real "start a game" path).
        MenuCommand.StartMap = ChangeLevel;
        MenuCommand.StartEditor = EditorMap;
        MenuCommand.Connect = OnConnect;

        // --- T50: the menu nav verbs + the live-match gameplay-command channel ---
        // The menu→match channel: route through the SAME path the console uses — the in-process listen-server
        // world as a CLIENT command (so join/spec/ready act on the local player), else the remote string-command
        // channel for a pure --connect client. This is what makes the pause-menu Join!/Spectate/Leave buttons
        // (and the team-select `cmd ...` buttons) actually reach the running match.
        MenuCommand.SendGameCommand = line =>
        {
            if (LocalRouteCommand(line) is null)
                _netGame?.SendStringCommand(line);
        };
        // QC gamestatus & (GAME_ISSERVER|GAME_CONNECTED): drives the Leave-match button's disabled state.
        MenuCommand.InMatch = () => MatchRunning;

        // QC m_goto(name, false): open OVER the menu, keep the menu open on close (nexposee/servers/profile/…).
        MenuCommand.OpenDialogOverlay = name => OpenMenuDialog(name, resumeOnClose: false);
        // QC m_goto(name, true): open + hide-the-menu-on-close (directmenu → drop back into the match on Back).
        MenuCommand.OpenDialog = name => OpenMenuDialog(name, resumeOnClose: true);
        // QC closemenu: pop the menu back one level (the dialog is the top screen when closemenu fires).
        MenuCommand.CloseDialog = _ => { if (_menu.CanPop) _menu.Pop(); };

        // [T28] QC menu_restart: rebuild the front-end (re-apply skin + re-translate) so a skin/language change shows.
        MenuCommand.MenuRestart = () => _menu.Restart();
    }

    /// <summary>
    /// Resolve a QC dialog name through <see cref="MenuDialogRegistry"/> and push it. <paramref name="resumeOnClose"/>
    /// reproduces QC <c>m_goto(name, true)</c> (directmenu): closing the dialog while a match is live resumes the
    /// match instead of returning to the pause menu. The <c>nexposee</c> name is special — it shows the main-menu
    /// fan as a full root screen rather than a framed sub-dialog, matching QC's "Main menu" button (the match
    /// stays connected; the nexposee just shows over it). Unknown names log (QC's invalid-command path).
    /// </summary>
    private void OpenMenuDialog(string name, bool resumeOnClose)
    {
        if (string.Equals(name, "nexposee", System.StringComparison.OrdinalIgnoreCase))
        {
            // QC keeps the match connected and shows the nexposee over it; ShowScreen replaces the stack with the
            // fan (Push'd dialogs from here layer on top, and Back collapses the fan — never tears the match down).
            _menu.ShowScreen(new MainMenu());
            _menu.Visible = true;
            return;
        }

        Control? screen = MenuDialogRegistry.Create(name);
        if (screen is null)
        {
            Log.Warn($"[Shell] menu_cmd: no dialog named '{name}'.");
            return;
        }
        if (resumeOnClose) _menu.PushResumeOnClose(screen);
        else _menu.Push(screen);
        _menu.Visible = true;
    }

    public override void _ExitTree()
    {
        // DP saves config.cfg at engine shutdown (Host_SaveConfig) — persist the archived cvars here so
        // settings edits survive a quit even without passing through a Back button.
        MenuState.SaveUserConfig();

        // Detach the static menu callbacks so a re-created shell (or test) doesn't double-fire.
        CreateGameScreen.StartGameRequested -= OnStartGame;
        MultiplayerScreen.Browser.ConnectRequested -= OnConnect;
    }

    /// <summary>
    /// Release the OS mouse grab whenever Xonotic isn't the focused application, and restore the gameplay grab
    /// when it regains focus. Without this, a match started (or left running) while the window is unfocused —
    /// a scripted/background <c>--host</c> launch, or a match that ends while alt-tabbed away — would still
    /// confine the cursor to the window's screen rectangle: the Win32 <c>ClipCursor</c> that
    /// <see cref="Input.MouseModeEnum.Captured"/> sets is system-wide and persists until focus next changes,
    /// trapping the pointer in other apps. <see cref="MouseCapture"/> ANDs the game's desired capture with this
    /// focus state. Godot propagates the application-focus notifications to every node, so the shell (always in
    /// the tree) is a reliable single listener.
    /// </summary>
    public override void _Notification(int what)
    {
        // Handle both the window-level (WM_WINDOW_FOCUS) and whole-app (APPLICATION_FOCUS) edges: Godot
        // delivers both to every node, and SetFocused → Apply is idempotent, so catching either keeps us
        // correct regardless of which a given platform emits.
        if (what == NotificationWMWindowFocusOut || what == NotificationApplicationFocusOut)
        {
            _windowFocused = false;
            MouseCapture.SetFocused(false);
            SyncAutoPause();   // #19: freeze a solo local game when the window loses focus / is minimized
        }
        else if (what == NotificationWMWindowFocusIn || what == NotificationApplicationFocusIn)
        {
            _windowFocused = true;
            MouseCapture.SetFocused(true);
            SyncAutoPause();   // ...and resume it on regaining focus
        }
    }

    // -------------------------------------------------------------------------------------------------
    //  Escape — toggle the in-game menu (only while a match is running)
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The single owner of the Escape→pause-menu toggle. Handled in <see cref="_UnhandledKeyInput"/> (Godot
    /// dispatches this BEFORE <c>_unhandled_input</c>) and the event is CONSUMED, so the gameplay bind path in
    /// <see cref="VortexArena.Game.Net.NetGame"/> — which runs in <c>_unhandled_input</c> and would otherwise
    /// also fire the <c>togglemenu</c> bind — never sees this Escape.
    /// Earlier-stage handlers still win: the console (<c>_Input</c>) eats Escape while open, and the key-rebind
    /// capture button (<c>_GuiInput</c>) eats it while capturing.
    ///
    /// <para><b>Why we toggle on the RELEASE edge, not the press.</b> While the mouse is
    /// <see cref="Input.MouseModeEnum.Captured"/>, the OS/Godot swallows the Escape <em>press</em> (it's used to
    /// break the pointer grab) — only the release reaches the app. A press-based toggle therefore silently
    /// dropped the very first Escape (mouse still captured), so the menu only opened from the second press on.
    /// The release edge always arrives, so toggling on it makes the first Escape open the menu reliably.</para>
    /// </summary>
    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Echo: false, Keycode: Key.Escape } key)
            return;
        if (!MatchRunning || ConsoleState.IsOpen)
            return; // at the main menu Escape does nothing; an open console owns its own Escape

        // Own BOTH edges so the gameplay bind path never also reacts to Escape, but only act on release.
        GetViewport().SetInputAsHandled();
        if (key.Pressed)
            return;

        // In-match Escape ownership, highest priority first — each acts on the RELEASE edge (the only edge that
        // reliably arrives while the mouse is captured, per the note above). Without these, cancelling the chat
        // prompt or pressing Escape in the HUD editor leaked to the generic pause menu instead:
        //   1. the chat prompt cancels (it deliberately does NOT consume Escape, so both edges reach here);
        if (Game.Hud.ChatPrompt.IsOpen)
        {
            _chatPrompt.Close();
            return;
        }
        //   1b. the interactive scoreboard: Escape closes it, and Escape WHILE the scoreboard key is held opens
        //       it (QC main.qc:545-551 checks S_TAB before falling through to the menu). Must be claimed here —
        //       Godot runs _UnhandledKeyInput before _UnhandledInput, and this method marks both Escape edges
        //       handled, so the play path's own handler never sees the key.
        if (Menu.MenuCommand.ScoreboardEscape?.Invoke() == true)
            return;
        //   2. the live HUD editor (no menu dialog up) opens its setup-exit dialog — QC menu_showhudexit —
        //      instead of the pause menu; with a dialog already up (_paused) fall through so Escape pops it.
        if (!_paused && MenuState.Cvars.GetFloat("_hud_configure") != 0f)
        {
            MenuCommand.Run("menu_showhudexit");
            return;
        }

        if (_paused)
        {
            // Inside a pushed sub-screen (Settings, …) Escape backs out one level; at the pause root it resumes.
            if (_menu.CanPop) _menu.Pop();
            else Resume();
        }
        else
        {
            OpenPauseMenu();
        }
    }

    /// <summary>DP <c>togglemenu</c> engine command (console / aliases like <c>team_auto</c>): toggle the
    /// in-game pause menu. <paramref name="mode"/> 0 = force close; anything else = toggle (open if closed,
    /// close/pop if open). The Escape KEY is handled directly in <see cref="_UnhandledKeyInput"/>, so this
    /// path is only reached by an explicit <c>togglemenu</c> command, never by the Escape bind.</summary>
    private void HandleToggleMenu(int mode)
    {
        if (!MatchRunning)
            return;
        if (mode == 0)
        {
            if (_paused) Resume();
            return;
        }
        if (_paused)
        {
            if (_menu.CanPop) _menu.Pop();
            else Resume();
        }
        else
        {
            OpenPauseMenu();
        }
    }

    // -------------------------------------------------------------------------------------------------
    //  Match lifecycle
    // -------------------------------------------------------------------------------------------------

    /// <summary>Menu "Start" / campaign / Instant Action handler — host a listen server for the chosen config
    /// (a real networked match the local client joins), so Create Game actually starts a playable server.</summary>
    private void OnStartGame(MatchConfig config)
    {
        Log.Info($"[Shell] start game: {config}");
        StartListenServer(config);
    }

    /// <summary>
    /// Open the no-net <see cref="ModelViewer"/> on a hero player model (CLI <c>--model &lt;name&gt;</c>): a thin
    /// scene that loads the model through the real skeletal path and lays out a turntable contact sheet for a
    /// windowed visual-QA screenshot. Reuses the menu's shared VFS (no second mount). Pair with
    /// <c>--screenshot</c> to capture and quit; <c>tools/visual-qa.sh</c> drives the per-model sweep.
    /// </summary>
    public void StartModelViewer(string modelName)
    {
        TeardownGame();

        _viewer = new ModelViewer
        {
            Name = "ModelViewer",
            ProcessMode = ProcessModeEnum.Pausable,
            ModelName = string.IsNullOrWhiteSpace(modelName) ? "erebus" : modelName,
            SharedVfs = MenuState.Vfs,
        };
        AddChild(_viewer);

        // The viewer builds synchronously in _Ready; just reveal the stage (mouse stays free — there's no
        // gameplay to capture the pointer for).
        _menu.Visible = false;
        _paused = false;
        GetTree().Paused = false;
        MouseCapture.SetWantCapture(false);
    }

    /// <summary>Open the in-game menu: show the pause screen and free the mouse. The match freezes ONLY for a
    /// solo local game (see <see cref="SyncAutoPause"/>); with remote clients connected it keeps running.</summary>
    private void OpenPauseMenu()
    {
        _menu.ShowScreen(new PauseMenu());
        _menu.Visible = true;
        _paused = true;
        MouseCapture.MenuWantsCursor = true; // survives NetGame's per-frame reassert on an unpaused tree
        MouseCapture.SetWantCapture(false);
        SyncAutoPause(); // freeze the sim iff this is a solo local listen game
    }

    /// <summary>Resume the match: hide the menu, recapture the mouse, and release any auto-pause.</summary>
    private void Resume()
    {
        if (!MatchRunning)
            return;
        _menu.Visible = false;
        _paused = false;
        MouseCapture.MenuWantsCursor = false; // clear BEFORE SetWantCapture so recapture lands this frame
        MouseCapture.SetWantCapture(true);
        SyncAutoPause();
    }

    /// <summary>QoL auto-pause (#19): freeze a SOLO local game while the player can't act on it — the pause menu
    /// is open, the console is open, or the window is unfocused/minimized. Gated on hosting a listen server with
    /// NO remote clients (a real multiplayer server must keep running for them) and on <c>cl_autopause</c> != 0
    /// (default on). The Shell owns <c>GetTree().Paused</c>: the match is <see cref="Node.ProcessModeEnum.Pausable"/>
    /// while the Shell is <see cref="Node.ProcessModeEnum.Always"/>, so this keeps running to RELEASE the pause.</summary>
    private void SyncAutoPause()
    {
        var cv = MenuState.Cvars;
        // Treat an unset cvar as ON (default-enabled) so no registration is required for the default behaviour.
        bool cvarOn = string.IsNullOrEmpty(cv.GetString("cl_autopause")) || cv.GetFloat("cl_autopause") != 0f;
        bool eligible = MatchRunning
            && _loadingScreen is null                        // not mid-load: pausing then would stall the handshake
            && (_netGame?.IsListenServer ?? false)
            && !(_netGame?.HasRemoteClients ?? false)
            && cvarOn;
        bool cantAct = _paused || ConsoleState.IsOpen || !_windowFocused;
        bool want = eligible && cantAct;
        if (GetTree().Paused != want)
            GetTree().Paused = want;
    }

    public override void _Process(double delta)
    {
        // #28: derive window focus from OS TRUTH every frame (not just the notification edges). A window LAUNCHED
        // into the background misses its focus edges entirely — Godot's flag sits stale-TRUE, the spawn-frame
        // capture then set the system-wide ClipCursor confine, and the pointer stayed trapped in the game's window
        // border while ANOTHER app was foreground, with no edge ever arriving to release it. Polling
        // WindowReallyFocused (Godot flag AND Win32 GetForegroundWindow) both releases such a confine within a
        // frame (SetFocused → Apply) and lets the #19 auto-pause treat a never-focused background launch as
        // unfocused (paused until the player actually clicks in). Skipped headless: no cursor to manage, and the
        // headless DisplayServer's focus answer must never pause an automated smoke/CI run.
        if (DisplayServer.GetName() != "headless")
        {
            bool osFocused = MouseCapture.WindowReallyFocused();
            if (osFocused != _windowFocused)
            {
                _windowFocused = osFocused;
                MouseCapture.SetFocused(osFocused);
            }
        }

        // The menu layer (main or pause) always owns the pointer: re-sync the MouseCapture menu override off
        // menu visibility every frame (not just the Open/Resume edges) so any other path that shows/hides the
        // menu keeps the cursor state consistent — same reassert-not-edge-latch reasoning as NetGame's cursor block.
        MouseCapture.MenuWantsCursor = _menu.Visible;

        // Per-frame so console open/close and a remote client joining/leaving (which changes eligibility) are
        // picked up even without a discrete edge — e.g. a remote joins while the host sits in the menu → release
        // the pause so they aren't frozen. Shell is ProcessModeEnum.Always, so this runs even while paused.
        SyncAutoPause();
    }

    /// <summary>True while a networked match (<see cref="VortexArena.Game.Net.NetGame"/> — listen server or
    /// remote client) is live. The no-net <see cref="ModelViewer"/> is intentionally NOT a "match" (no pause menu).</summary>
    private bool MatchRunning => _netGame is not null;

    /// <summary>
    /// A live match's connection failed before the player spawned (server reject, dropped link, or connect
    /// timeout — see <see cref="VortexArena.Game.Net.NetGame.OnConnectionFailed"/>). Tear it down, return to the
    /// main menu, and put a dismissable notice in front of the player naming the target and the reason — instead
    /// of the old behaviour where the loading screen sat at "Connecting…" forever. The <paramref name="net"/>
    /// guard drops a stale callback from a game the user already replaced (a second Create/Connect mid-failure).
    /// </summary>
    private void HandleConnectionFailed(VortexArena.Game.Net.NetGame net, string reason, string target)
    {
        if (net != _netGame)
            return; // superseded by a newer match; its own failure (if any) will drive the menu

        // Unattended runs exit instead of returning to a menu, for the same reason the port-bind failure above
        // does: a dedicated server or a headless run has nobody to read the notice and no input to dismiss it,
        // so the menu branch leaves the process alive and idle where a supervisor needs a failure. Plain
        // non-zero rather than a DS-4 code — DS-4 enumerates BOOT failures (2 = port in use, 3 = map not
        // found), and a handshake that was refused or timed out is not one of those.
        if (IsUnattended)
        {
            Log.Severe($"[Shell] connection to '{target}' failed: {reason}; exiting (nothing here can prompt).");
            GetTree().Quit(1);
            return;
        }

        Log.Warn($"[Shell] connection to '{target}' failed: {reason}");
        ReturnToMainMenu();
        _menu.Push(new DialogConnectionFailed(target, reason));
    }

    /// <summary>Disconnect: tear the match down, persist preferences, and show the main menu again.</summary>
    private void ReturnToMainMenu()
    {
        TeardownGame();
        DismissLoadingScreen();
        MenuState.SaveUserConfig();

        // Reinstall a clean menu-time facade (empty world) so no stale match entities linger behind the menu;
        // the shared cvar store is preserved so settings persist across the round-trip.
        Api.Services = new EngineServices(new CollisionWorld(), MenuState.Cvars);

        _menu.ShowScreen(new MainMenu());
        _menu.Visible = true;
        _paused = false;
        GetTree().Paused = false;
        MouseCapture.SetWantCapture(false);
    }

    private void TeardownGame()
    {
        // Close the messagemode prompt if the match ends while it's open — otherwise the Shell-lifetime overlay
        // (CanvasLayer 90, above the menu) keeps drawing over the main menu and its _Input eats every keystroke,
        // and the static IsOpen leaks into the next match (movement/fire suppressed + BUTTON_CHAT forced).
        _chatPrompt?.Close();

        if (_viewer is not null)
        {
            _viewer.QueueFree();
            _viewer = null;
        }
        if (_netGame is not null)
        {
            // Dispose the transports synchronously (releases the listen server's UDP port now) BEFORE the
            // deferred free, so an immediate re-host can bind the same port this frame.
            _netGame.Shutdown();
            _netGame.QueueFree();
            _netGame = null;

            // --observe is a one-session boot capture: disarm when that session ENDS (not on the boot-path
            // TeardownGame that runs before the first session exists), so a later menu-created game
            // auto-joins normally and doesn't inherit a camera pinned at the previous map's coordinates.
            Net.ObserverCamera.Disarm();
        }
    }

    // -------------------------------------------------------------------------------------------------
    //  Networked play — join a server, or host a listen server and self-connect (the real bring-up)
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Server-browser / address connect: build a REAL networked client (<see cref="VortexArena.Game.Net.NetGame"/>
    /// — ClientNet + prediction + the ClientWorld render bridge + a first-person camera following the predicted
    /// local player + a basic HUD/crosshair/radar) and connect to <paramref name="address"/>. Parses
    /// <c>host[:port]</c> (default 26000) and reuses the menu's shared VFS + cvar store so models/sounds resolve
    /// and prediction reads the user's physics cvars.
    /// </summary>
    private void OnConnect(string address) => ConnectToServer(address);

    /// <summary>Tear down any match and spin up a client NetGame connected to <paramref name="address"/>.</summary>
    public async void ConnectToServer(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            Log.Warn("[Shell] connect: empty address.");
            return;
        }
        Log.Info($"[Shell] connecting to {address}.");
        TeardownGame();
        ShowLoadingScreen(address);

        // Hide the menu (and unfreeze the tree) NOW so the loading screen is the only thing on screen during the
        // blocking connect — not the menu frozen behind the overlay. The cursor stays free until the player spawns.
        EnterMatchView();

        var net = new VortexArena.Game.Net.NetGame
        {
            Name = "NetClient",
            // Keep processing while the in-game menu pauses the tree, so the netcode keeps pumping (the link
            // would otherwise time out and prediction freeze). NetGame gates movement input on the pause itself.
            ProcessMode = ProcessModeEnum.Always,
        };
        net.ConfigureClient(address, ResolvePlayerName(), MenuState.Vfs, MenuState.Cvars, MenuState.SharedAssets);
        net.LoadingScreen = _loadingScreen;
        net.DismissLoadingScreen = DismissLoadingScreen;
        net.OnConnectionFailed = reason => HandleConnectionFailed(net, reason, address);
        WireConsoleToNet(net);
        _netGame = net;

        // Wait for the loading screen to actually be PAINTED before kicking off the blocking connect.
        await WaitForFramePainted();
        if (_netGame != net) return; // abandoned: TeardownGame was called while we awaited

        AddChild(net);
    }

    /// <summary>
    /// Pick the UDP port the listen server will bind. Godot's ENet binds with address reuse, so a taken port
    /// "binds" anyway and the self-connect then hangs on the loading screen forever (the packets go to whoever
    /// bound first — e.g. a DarkPlaces client, which also defaults to 26000). So probe first: the desired port
    /// if free; otherwise auto-increment up to <see cref="HostPortScanLimit"/> — unless the port was pinned via
    /// <c>--port</c> (<see cref="BootPortExplicit"/>), which must be honoured exactly. Returns -1 (with the
    /// error already logged) when no port is usable.
    /// </summary>
    private int ResolveHostPort()
    {
        if (VortexArena.Game.Net.NetTransport.Server.IsPortFree(BootPort))
            return BootPort;

        if (BootPortExplicit)
        {
            Log.Severe($"[Shell] UDP {BootPort} (--port) is already in use by another program — " +
                      "close it or pick a different --port.");
            return -1;
        }

        // A DEDICATED server never moves. Its port is half of the address players connect to and the
        // browser advertises, so quietly hosting on 26001 does not rescue the server — it strands it,
        // running and healthy at an address nobody was told about, which is a worse failure than not
        // starting because nothing reports it. Auto-increment is a LISTEN-server convenience (somebody
        // is sitting there, and the game they wanted is the game they get); a dedicated host has an
        // operator instead, and an operator can be told.
        if (VortexArena.Game.Net.NetGame.DedicatedRequested)
        {
            Log.Severe($"[Shell] UDP {BootPort} is already in use, and a dedicated server will not move to " +
                       "another port — free it, or pass --port <n> to host somewhere else deliberately.");
            return -1;
        }

        for (int candidate = BootPort + 1; candidate <= HostPortScanLimit; candidate++)
        {
            if (VortexArena.Game.Net.NetTransport.Server.IsPortFree(candidate))
            {
                Log.Warn($"[Shell] UDP {BootPort} is in use by another program — hosting on {candidate} instead.");
                return candidate;
            }
        }

        Log.Severe($"[Shell] no free host port: UDP {BootPort}..{HostPortScanLimit} are all in use.");
        return -1;
    }

    /// <summary>
    /// Host a LISTEN SERVER for the chosen config — boot a <see cref="VortexArena.Server.GameWorld"/> + a
    /// <see cref="VortexArena.Game.Net.ServerNet"/> in-process (filled with the config's bots), then self-connect a
    /// networked client to 127.0.0.1. This is the "Create Game" / <c>map</c> path, and also the boot path for
    /// <c>--map</c> (a 0-bot listen server — the consolidated local-match path). Reuses the menu's shared VFS + cvar store.
    /// </summary>
    public async void StartListenServer(MatchConfig config)
    {
        Log.Info($"[Shell] hosting listen server: {config}");
        TeardownGame(); // frees our own previous server's port before the probe below
        int hostPort = ResolveHostPort();
        if (hostPort < 0)
        {
            // No bindable port (a pinned --port is taken, a dedicated server's port is taken, or the whole
            // scan range is busy). ResolveHostPort has already logged which.
            //
            // Unattended runs EXIT rather than showing this. A dedicated server or a headless run has no
            // player to read a modal and no input to dismiss one, so the dialog branch would leave the
            // process alive, idle, and sitting on a menu nobody can see — a hang where a supervisor needs a
            // failure. Exit 2 is the code docs/RUNNING.md (DS-4) already documents for "UDP port in use";
            // until now nothing implemented it, so systemd Restart=on-failure had nothing to act on.
            if (IsUnattended)
            {
                Log.Severe("[Shell] cannot host without a port; exiting (DS-4 exit 2 = UDP port in use).");
                GetTree().Quit(HostPortInUseExit);
                return;
            }

            // Interactive: surface it the same way a failed connect does — back to the menu with a visible
            // reason, not a silent no-op.
            ReturnToMainMenu();
            _menu.Push(new DialogConnectionFailed(config.Map ?? "",
                BootPortExplicit
                    ? $"UDP port {BootPort} is already in use. Close the other program, or host on a different --port."
                    : $"No free network port ({BootPort}–{HostPortScanLimit}) was available to host on."));
            return;
        }
        ShowLoadingScreen(config.Map ?? "");

        // Hide the menu (and unfreeze) NOW so the loading screen is the only thing on screen during the blocking
        // load, not the menu frozen behind it. The cursor stays free until the player spawns.
        EnterMatchView();

        // Apply the chosen match limits to the shared cvars so the hosted world reads them.
        if (config.TimeLimit > 0) MenuState.Cvars.Set("timelimit", config.TimeLimit.ToString());
        if (config.FragLimit > 0) MenuState.Cvars.Set("fraglimit", config.FragLimit.ToString());
        // QC menu_loadmap_prepare runs `maxplayers <n>` just before the map loads (commands.cfg:114). Same moment
        // here: the NetGame below adopts the pending count as it starts. 0 = the caller expressed no preference
        // (a CLI/campaign boot), so whatever the config tree or --maxplayers chose stands.
        if (config.Slots > 0) VortexArena.Common.Config.ServerSlots.Set(config.Slots);

        var net = new VortexArena.Game.Net.NetGame
        {
            Name = "ListenServer",
            ProcessMode = ProcessModeEnum.Always, // the hosted server must keep ticking under the pause menu
        };
        net.ConfigureListenServer(
            map: config.Map ?? "",
            gametype: string.IsNullOrWhiteSpace(config.Gametype) ? "dm" : config.Gametype,
            botCount: config.BotCount,
            botSkill: config.BotSkill,
            port: hostPort,
            playerName: ResolvePlayerName(),
            serverName: MenuState.Cvars.GetString("hostname") is { Length: > 0 } hn ? hn : "Vortex Arena Listen Server",
            vfs: MenuState.Vfs,
            cvars: MenuState.Cvars,
            campaignName: config.CampaignId ?? "",   // non-empty → the server boots this as a campaign level
            campaignIndex: config.CampaignIndex,
            sharedAssets: MenuState.SharedAssets);   // persist the model/sound/material caches across maps & servers
        net.LoadingScreen = _loadingScreen;
        net.DismissLoadingScreen = DismissLoadingScreen;
        net.OnConnectionFailed = reason => HandleConnectionFailed(net, reason, config.Map ?? "");
        WireConsoleToNet(net);
        _netGame = net;

        // Wait for the loading screen to actually be PAINTED before kicking off the blocking map load.
        await WaitForFramePainted();
        if (_netGame != net) return; // abandoned: TeardownGame was called while we awaited

        AddChild(net);
    }

    /// <summary>
    /// Yield until the GPU has actually painted a frame (so any UI changes — e.g. the loading screen —
    /// are on screen before we hand control back to a synchronous, blocking caller). Uses
    /// <c>RenderingServer.frame_post_draw</c>, which fires after the renderer submits the frame.
    /// Falls back to two <see cref="SceneTree.ProcessFrame"/> ticks under <c>--headless</c>: the
    /// RenderingServer singleton still EXISTS there (dummy renderer), but the main loop never calls
    /// <c>RenderingServer.draw()</c> when no window can draw — so <c>frame_post_draw</c> never fires
    /// and awaiting it would hang the boot forever (the old "headless --host never loads the map" bug).
    /// </summary>
    private async System.Threading.Tasks.Task WaitForFramePainted()
    {
        var rs = RenderingServer.Singleton;
        if (rs is not null && DisplayServer.GetName() != "headless")
        {
            // Two cycles to be safe: the first may resume during the same iteration that scheduled the
            // await (depending on where in the main loop we were called from); the second is a guaranteed
            // post-draw fence.
            await ToSignal(rs, RenderingServer.SignalName.FramePostDraw);
            await ToSignal(rs, RenderingServer.SignalName.FramePostDraw);
        }
        else
        {
            // Headless: nothing is ever painted. Two process_frame ticks at least flushes one full iteration.
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    /// <summary>The local player's display name (QC <c>_cl_name</c>/<c>name</c> cvar), defaulting to "player".</summary>
    private static string ResolvePlayerName()
    {
        string n = MenuState.Cvars.GetString("_cl_name");
        if (string.IsNullOrWhiteSpace(n)) n = MenuState.Cvars.GetString("name");
        return string.IsNullOrWhiteSpace(n) ? "player" : n;
    }

    /// <summary>Hide the menu and unfreeze the tree for a match that is about to load. The cursor is left FREE:
    /// this runs at the START of the load (the loading screen is the only thing on screen), and capturing the
    /// pointer for a whole windowed map-load would trap it off-screen. NetGame grabs the cursor for mouse-look the
    /// frame the local player actually spawns (its per-frame reassert, gated on the loading screen closing).</summary>
    private void EnterMatchView()
    {
        _menu.Visible = false;
        _paused = false;
        GetTree().Paused = false;
        MouseCapture.SetWantCapture(false);
    }

    // -------------------------------------------------------------------------------------------------
    //  Loading screen (DP SCR_DrawLoadingScreen)
    // -------------------------------------------------------------------------------------------------

    /// <summary>Show the Darkplaces-style loading screen (gfx/loading.tga + progress bar + status text)
    /// on a high CanvasLayer above the HUD, before a match enters the tree.</summary>
    private void ShowLoadingScreen(string mapName)
    {
        DismissLoadingScreen();

        _loadingScreen = new LoadingScreen();
        _loadingScreen.UpdateProgress(0f, "Loading...");

        // CanvasLayer above everything (same layer 100 the old black connect overlay used)
        _loadingLayer = new CanvasLayer { Name = "LoadingLayer", Layer = 100 };
        _loadingLayer.AddChild(_loadingScreen);
        AddChild(_loadingLayer);

        _loadingScreen.SetMapName(mapName);
    }

    /// <summary>Tear down the loading screen layer (idempotent).</summary>
    internal void DismissLoadingScreen()
    {
        if (_loadingLayer is not null && GodotObject.IsInstanceValid(_loadingLayer))
        {
            _loadingLayer.QueueFree();
        }
        _loadingLayer = null;
        _loadingScreen = null;
    }

    // -------------------------------------------------------------------------------------------------
    //  Console ↔ match wiring
    // -------------------------------------------------------------------------------------------------

    /// <summary>Point a freshly-created match's input + console-output hooks at the shared console: bound keys run
    /// one-shot commands through the shared interpreter, and the server's console replies print in the overlay.</summary>
    private void WireConsoleToNet(VortexArena.Game.Net.NetGame net)
    {
        net.RunCommand = MenuState.Interp!.ExecuteLine;
        net.ConsolePrint += _console.Print;
        // A listen-server changelevel (map/gotomap/rotation) reboots the server on the new map, preserving the
        // mode + bots — and, on a campaign auto-advance (win → next level), the campaign id + index so the next
        // level comes up in campaign mode. The event is emitted deferred by NetGame, so this restart runs at idle.
        net.MapChangeRequested += (map, gametype, bots, skill, campId, campIdx) =>
            StartListenServer(new MatchConfig
            {
                Map = map, Gametype = gametype, BotCount = bots, BotSkill = skill,
                CampaignId = campId, CampaignIndex = campIdx,
            });
    }

    /// <summary>
    /// DP <c>map</c>/<c>changelevel</c>: while a listen server is running, change to <paramref name="map"/> keeping
    /// the current gametype + bot fill (routed through the server's deferred change-level path); at the menu (no
    /// match), start a fresh listen server on it. A pure <c>--connect</c> client has no local server to changelevel
    /// (the real server owns the map), so this no-ops there.
    /// </summary>
    private void ChangeLevel(string map) => ChangeLevel(map, null);

    /// <param name="gametype">Gametype to switch to, or null to keep the current one.</param>
    private void ChangeLevel(string map, string? gametype)
    {
        if (string.IsNullOrWhiteSpace(map))
            return;
        if (_netGame is { ServerWorld: not null })
            _netGame.RequestMapChange(map, gametype);                        // in a match → server-side changelevel
        else if (_netGame is null)
            StartListenServer(new MatchConfig { Map = map, Gametype = gametype ?? "dm" });
    }

    /// <summary>
    /// <c>editor [map]</c> — the map editor as a destination, alongside <c>map</c> and <c>devmap</c>.
    ///
    /// With no argument it re-hosts the map already running, which is the point: you are looking at
    /// something you want to change, and getting to the editor should not mean quitting to the menu and
    /// starting over. The gametype rides the same changelevel path every map switch uses.
    /// </summary>
    private void EditorMap(string map)
    {
        string target = !string.IsNullOrWhiteSpace(map) ? map!
            : _netGame?.CurrentMap ?? BootMap ?? "";
        if (string.IsNullOrWhiteSpace(target))
        {
            _console.Print("editor: no map running — use `editor <map>`.");
            return;
        }
        ChangeLevel(target, "editor");
    }

    /// <summary>
    /// The console's gameplay-command router: run <paramref name="line"/> on the in-process listen-server world
    /// as a CLIENT command (so kill/say/team act on the local player) and return its output. Returns null when
    /// there is no local world (a pure <c>--connect</c> client or no match) so the console falls back to the
    /// remote string-command channel.
    /// </summary>
    private string? LocalRouteCommand(string line)
    {
        // T47 integration wire-up: the listen-server operator's in-game console is the HOST, so it runs as the
        // server console (isServerConsole: true) — without this flag the new client-command privilege gate would
        // reject the host's own kick/map/set/endmatch/etc. (a regression vs pre-T47). caller stays LocalServerPlayer
        // so kill/say/team still act on the host's player; the remote-client path (ServerNet.cs) stays gated.
        // WS1: routed through NetGame so the execute takes the sim gate when sv_threaded is on — a bare
        // Commands.Execute here mutated the worker-owned world from the main thread (`kill` mid-bot-combat).
        return _netGame?.ExecuteHostConsoleCommand(line);
    }

    /// <summary>
    /// Fallback for the console's gameplay-command router when there is no in-process server: forward to the
    /// connected remote server (DP <c>clc_stringcmd</c>) on a pure <c>--connect</c> client, otherwise PRINT a
    /// hint so the user gets feedback instead of silent acceptance — the menu has no live game to route to,
    /// but the cvar/exec/bind side of the console always processes regardless.
    /// </summary>
    private void RouteRemoteCommand(string line)
    {
        if (_netGame is not null)
            _netGame.SendStringCommand(line);
        else
            VortexArena.Common.Diagnostics.Log.Help($"\"{line}\": no server — start a match (`map <name>`) or `connect <addr>` first.");
    }
}
