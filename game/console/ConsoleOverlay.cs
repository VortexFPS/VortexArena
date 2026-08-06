using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using VortexArena.Common.Config;
using VortexArena.Common.Diagnostics;
using VortexArena.Engine.Console;
using VortexArena.Engine.Simulation;
using VortexArena.Game.Menu;

namespace VortexArena.Game.Console;

/// <summary>
/// The in-game developer console overlay — the C# successor to DP's drop-down console (<c>Con_DrawConsole</c> +
/// <c>Con_DrawInput</c> + <c>Key_Console</c>). A high <see cref="CanvasLayer"/> (above the menu/HUD) holding a
/// drop-down with a scrollback <see cref="RichTextLabel"/> and an input <see cref="LineEdit"/>. Backtick
/// (<c>`</c>) toggles it; typed lines run through the shared <see cref="ConfigInterpreter"/> (the same buffer
/// that loads the <c>.cfg</c> tree), so the console interprets commands EXACTLY as a config file would —
/// <c>set</c>/<c>seta</c>/<c>alias</c>/<c>exec</c>/<c>$cvar</c> + the console/cvar builtins
/// (<see cref="ConsoleCommands"/>) + gameplay commands routed to the live server.
///
/// <para>It mirrors the whole <see cref="Log"/> stream into the scrollback (so every <c>LOG_*</c> line is
/// visible like DP) and renders Quake <c>^</c> colour codes via <see cref="Log.ToBBCode"/>.</para>
///
/// <h3>The DP presentation this reproduces</h3>
/// <list type="bullet">
///   <item><b>The background</b> is Xonotic's own art, not a flat panel: up to three layers
///     (<c>gfx/conback</c>, <c>conback2</c>, <c>conback3</c>) composited at
///     <c>scr_conalpha × scr_conalpha{,2,3}factor</c>, tinted by <c>scr_conbrightness</c>, each scrolling at its
///     own <c>scr_conscroll*_x/y</c> rate. Each layer is drawn full-screen-sized with its BOTTOM edge at the
///     console's bottom (DP <c>Con_DrawConsole</c>), clipped by the drop-down — so it looks identical whatever
///     <c>scr_conheight</c> is. Missing art falls back to DP's flat black fill.</item>
///   <item><b>The geometry</b> is cvar-driven: <c>scr_conheight</c> (fraction of the screen) and
///     <c>con_textsize</c> (in the <c>vid_conwidth × vid_conheight</c> virtual canvas, so the text is the same
///     apparent size at every resolution). Ctrl+= / Ctrl+- / Ctrl+0 zoom it live, as in DP.</item>
///   <item><b>The furniture</b>: the <c>]</c> prompt beside the input line, and the build version drawn in red
///     on the console's last line (DP draws <c>engineversion</c> there).</item>
///   <item><b>A monospace face</b> (DejaVu Sans Mono, which Xonotic ships and DP uses for <c>FONT_CONSOLE</c>),
///     so the column-formatted completion lists line up.</item>
/// </list>
///
/// <h3>Input model</h3>
/// <para><see cref="_Input"/> grabs backtick (toggle), Escape (close) and the modified mouse wheel; everything
/// else arrives at the focused input line and is routed through <see cref="HandleKey"/> — one place holding the
/// whole <c>Key_Console</c> + <c>Key_Parse_CommonKeys</c> key map (history, history search, scrollback paging,
/// completion, text zoom, line editing). Keys DP has that Godot's <see cref="LineEdit"/> already implements
/// natively (word-wise caret motion, Home/End, clipboard, undo) are left to it.</para>
/// <para>The play path (<c>NetGame</c>) independently freezes gameplay input on
/// <see cref="ConsoleState.IsOpen"/> (its polled WASD is not stopped by event consumption). The mouse is freed
/// while open and restored via the host's <c>shouldCaptureOnClose</c> on close.</para>
/// </summary>
public partial class ConsoleOverlay : CanvasLayer
{
    private const int MaxParagraphs = 2048;

    /// <summary>Where the input history is kept between sessions (DP <c>darkplaces_history.txt</c>).</summary>
    private static string HistoryPath => UserPaths.Resolve("console_history.txt");

    private Control _panel = null!;
    private ConsoleBackground _background = null!;
    private RichTextLabel _output = null!;
    private Label _prompt = null!;
    private ConsoleLineEdit _input = null!;
    private Label _version = null!;

    private ConfigInterpreter? _interp;
    private CvarService? _cvars;
    private ConsoleCommands? _commands;
    private Func<bool>? _shouldCaptureOnClose;
    // Kept (not just handed to ConsoleCommands) so a host command registered here can take the SAME route a
    // gameplay command does — `lsmaps` asks the live server when there is one and answers locally when not.
    private Func<string, string?>? _localRouter;
    private Action<string>? _remoteSender;

    /// <summary>The input history (DP's <c>Key_History_*</c> ring), loaded at Initialize and saved on teardown.</summary>
    private readonly ConsoleHistory _history = new();

    private Action<LogEntry>? _logSubscription;  // EntryRecorded handler we installed (detached on teardown)
    private Action<string>? _cvarChangedSub;     // CvarService.Changed handler watching `developer`
    private int _renderedDeveloper = -1;         // dev level the scrollback was last rendered at
    private bool _eatEscapeRelease;              // true between the Escape press we consumed and its matching release

    // Last applied style, so the per-frame refresh only touches the theme when something actually changed.
    private int _styledFontPx = -1;
    private float _styledHeight = -1f;
    private Vector2 _styledViewport = new(-1, -1);

    /// <summary>True while the drop-down is showing (mirrors <see cref="ConsoleState.IsOpen"/>).</summary>
    public bool IsOpen => _panel.Visible;

    public override void _Ready()
    {
        Layer = 128;                              // above the menu (10), HUD (5), engine overlay (120)
        ProcessMode = ProcessModeEnum.Always;     // usable even while the in-game menu pauses the tree
        BuildUi();

        // Subscribe to the Log facade's ALWAYS-ON ring buffer. The buffer captures every Log.* call BEFORE the
        // `developer` gate, so a Trace emitted at developer 0 still lands in the scrollback — switching to
        // `set developer 1` reveals it retroactively (see RebuildScrollback). The live sink (Main._Ready's
        // GD.PrintRich → editor Output) is left alone; we read the buffer in parallel.
        _logSubscription = OnLogEntry;
        Log.EntryRecorded += _logSubscription;

        // Replay everything captured BEFORE we attached (MenuState.Boot, registries, etc.) so the console shows
        // the boot log even on its first open. Rendered at the current developer level — when the cvar changes
        // later we re-render the whole buffer.
        RebuildScrollback();
    }

    public override void _ExitTree()
    {
        if (_logSubscription != null)
        {
            Log.EntryRecorded -= _logSubscription;
            _logSubscription = null;
        }
        if (_cvarChangedSub != null && _cvars != null)
        {
            _cvars.Changed -= _cvarChangedSub;
            _cvarChangedSub = null;
        }
        SaveHistory();
    }

    /// <summary>
    /// Wire the console to the shared command buffer + cvar store and the host hooks. Called once by
    /// <see cref="Shell"/> after the overlay is in the tree. <paramref name="localRouter"/> runs a gameplay
    /// command on the in-process listen-server world (null on a pure client → falls to
    /// <paramref name="remoteSender"/>); <paramref name="shouldCaptureOnClose"/> tells the console whether to
    /// recapture the mouse when it closes (true only when a match is live and not paused).
    /// </summary>
    public void Initialize(
        ConfigInterpreter interp,
        CvarService cvars,
        Func<string, string?>? localRouter,
        Action<string>? remoteSender,
        Func<bool> shouldCaptureOnClose)
    {
        _interp = interp;
        _cvars = cvars;
        _shouldCaptureOnClose = shouldCaptureOnClose;
        _localRouter = localRouter;
        _remoteSender = remoteSender;
        _commands = new ConsoleCommands(interp, cvars, Print, Clear, localRouter, remoteSender);
        RegisterHostCommands(interp);
        WireCompletionSources(_commands.Completion);

        // The console-surface commands need things only the overlay owns: the rendered scrollback (condump),
        // somewhere to write it, and the live input history.
        // `condump` dumps the LOG BUFFER rendered at the current developer level, not the RichTextLabel's parsed
        // text: the label's BBCode has already been consumed by the parser, so its text has no colour codes left
        // for `condump_stripcolors` to decide about. The buffer holds the raw `^`-coded lines — DP's console
        // buffer, which is exactly what Con_ConDump_f writes.
        _commands.ScrollbackProvider = BuildScrollbackText;
        _commands.FileWriter = WriteUserFile;
        _commands.History = _history;

        LoadHistory();

        // Watch the `developer` cvar live: when it changes, re-render the entire scrollback so Trace/Debug
        // entries previously hidden become visible (and vice-versa). The buffer itself keeps everything.
        _cvarChangedSub = name =>
        {
            if (string.Equals(name, "developer", StringComparison.Ordinal))
                Callable.From(RebuildScrollback).CallDeferred();
        };
        cvars.Changed += _cvarChangedSub;

        // Re-render now that we know the live cvar (the boot replay in _Ready ran against dev 0 by default).
        RebuildScrollback();
        RefreshStyle(force: true);
    }

    /// <summary>
    /// Give Tab completion the three catalogs only the client can answer for: the mounted content tree (DP's
    /// <c>con_completion_&lt;command&gt;</c> file patterns, e.g. <c>exec *.cfg</c>), the installed maps
    /// (<c>map</c>/<c>chmap</c>/<c>gotomap</c>…), and the bindable key names.
    /// </summary>
    private void WireCompletionSources(CommandCompletion completion)
    {
        completion.MapNames = () => MapList.Available();
        completion.KeyNames = () => BindInput.CompletionKeyNames;
        completion.NickNames = () => MenuCommand.PlayerNames?.Invoke() ?? Array.Empty<string>();
        completion.FileSearch = glob =>
        {
            VortexArena.Formats.Vfs.VirtualFileSystem? vfs = MenuState.Vfs;
            if (vfs is null)
                return Array.Empty<string>();
            try { return SearchContent(vfs, glob); }
            catch (Exception ex)
            {
                Log.Trace($"[console] completion file search for \"{glob}\" failed: {ex.Message}");
                return Array.Empty<string>();
            }
        };
    }

    /// <summary>How many completion candidates one glob may return before we stop scanning the content tree.</summary>
    private const int MaxCompletionFiles = 2000;

    /// <summary>
    /// DP <c>FS_Search</c> for the completion patterns: expand one glob against the mounted content tree. A
    /// pattern ending in <c>/</c> asks for DIRECTORIES instead — the VFS index is flat (file paths only), so
    /// those are derived from the path segments, which is how DP's own directory listing behaves from the
    /// player's side.
    ///
    /// <para>Only the literal head of the pattern (up to its first wildcard, cut back to the last <c>/</c>) is
    /// used to narrow the index scan; the full glob then filters. As in DP, <c>*</c> crosses <c>/</c>, so
    /// <c>exec *.cfg</c> offers nested configs too.</para>
    /// </summary>
    private static IReadOnlyList<string> SearchContent(VortexArena.Formats.Vfs.VirtualFileSystem vfs, string glob)
    {
        bool wantDirs = glob.EndsWith("/", StringComparison.Ordinal);
        string pattern = (wantDirs ? glob[..^1] : glob).ToLowerInvariant();

        int star = pattern.IndexOfAny(new[] { '*', '?' });
        string literalHead = star < 0 ? pattern : pattern[..star];
        int slash = literalHead.LastIndexOf('/');
        string scanPrefix = slash < 0 ? "" : literalHead[..(slash + 1)];

        var results = new List<string>();
        if (wantDirs)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in vfs.Find(scanPrefix))
            {
                int at = key.IndexOf('/', scanPrefix.Length);
                if (at < 0)
                    continue;
                string dir = key[..at];
                if (seen.Add(dir) && ConsoleSearch.Glob(dir.ToLowerInvariant(), pattern))
                {
                    results.Add(dir + "/");
                    if (results.Count >= MaxCompletionFiles)
                        break;
                }
            }
            return results;
        }

        foreach (string key in vfs.Find(scanPrefix))
        {
            if (!ConsoleSearch.Glob(key.ToLowerInvariant(), pattern))
                continue;
            results.Add(key);
            if (results.Count >= MaxCompletionFiles)
                break;
        }
        return results;
    }

    /// <summary>
    /// Reset the renderer by reloading the current map. That IS the reset here: the settings a renderer
    /// restart is expected to pick up (gl_picmip and gl_texturecompression as each texture decodes,
    /// r_subdivisions_tolerance at patch tessellation, r_shadow_world_casts at world-cell creation) are all
    /// consumed while the map is built, and nothing short of rebuilding it re-applies them.
    /// </summary>
    /// <summary>
    /// Resolves the map the CLIENT is in. Wired by the Shell to <c>NetGame.CurrentMap</c>, the same source the
    /// missing-textures command uses. NOT the "mapname" cvar: that is set on the listen SERVER's private store
    /// (NetGame sets it on _serverWorld.Services.Cvars), which the shared menu/console store never sees - so
    /// reading it here reported "no map loaded" while standing on one.
    /// </summary>
    public static Func<string>? CurrentMapResolver { get; set; }

    private void ResetRenderer(string who)
    {
        string map = CurrentMapResolver?.Invoke() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(map))
            map = MenuState.Cvars.GetString("mapname");   // fallback: a store that happens to carry it
        if (string.IsNullOrWhiteSpace(map))
        {
            // Distinguish the two ways this can be empty, because they need different fixes: the resolver
            // never being installed is a wiring bug, whereas it returning empty just means no match is running.
            Print(CurrentMapResolver is null
                ? $"{who}: no map resolver installed (wiring bug - Shell should set CurrentMapResolver)."
                : $"{who}: no map loaded - these settings apply the next time a map loads.");
            return;
        }

        Print($"{who}: reloading {map} to re-apply the map-build render settings.");
        MenuCommand.StartMap?.Invoke(map);
    }

    /// <summary>Engine/host actions that need the Godot front-end (DP engine commands the console exposes). Wired
    /// here (not in the Godot-free <see cref="ConsoleCommands"/>) through the menu's existing host hooks.</summary>
    private void RegisterHostCommands(ConfigInterpreter interp)
    {
        // (N7) .rtlights authoring: status / reload / import-from-entities / save. Registered here with the
        // other host commands because they need the live client renderer, not just the config store.
        Client.RtLightsCommands.Register(interp, Print);

        interp.RegisterCommand("quit", _ => MenuCommand.Quit?.Invoke(), "exit the game");
        interp.RegisterCommand("exit", _ => MenuCommand.Quit?.Invoke(), "exit the game");
        interp.RegisterCommand("disconnect", _ => MenuCommand.Disconnect?.Invoke(),
            "leave the current match and return to the menu");
        interp.RegisterCommand("connect", a =>
        {
            if (a.Count >= 2) MenuCommand.Connect?.Invoke(a[1]);
            else Print("usage: connect <address>");
        }, "join a server: connect <address>");
        interp.RegisterCommand("map", a =>
        {
            if (a.Count >= 2) MenuCommand.StartMap?.Invoke(a[1]);
            else Print("usage: map <name>");
        }, "host a match on a map: map <name>");
        interp.RegisterCommand("devmap", a => { if (a.Count >= 2) MenuCommand.StartMap?.Invoke(a[1]); },
            "host a match on a map with cheats available: devmap <name>");
        // `editor [map]` sits beside map/devmap because it IS a map command — same changelevel path, only
        // the gametype differs. No argument re-hosts whatever is running, which is the whole point.
        interp.RegisterCommand("editor", a => MenuCommand.StartEditor?.Invoke(a.Count >= 2 ? a[1] : string.Empty),
            "open the in-game map editor, optionally on a named map: editor [name]");

        // DP Con_ToggleConsole_f — the command the `toggleconsole` bind (and the stock ESCAPE handling) calls.
        // It existed as a key but not as a command, so `bind F1 toggleconsole` reached the router as an unknown
        // gameplay command.
        interp.RegisterCommand("toggleconsole", _ => Callable.From(Toggle).CallDeferred(),
            "show or hide the console");

        // QC Cmd_Scoreboard_SetFields (client/hud/panel/scoreboard.qc:767), exposed in Base as the
        // `scoreboard_columns_set` client command: set the scoreboard's column layout. No argument re-applies the
        // saved `scoreboard_columns`; "default" / "all" select the two presets; anything else is a literal spec
        // ("ping pl name | score deaths"). The scoreboard reads the cvar live, so writing it IS the command.
        interp.RegisterCommand("scoreboard_columns_set", a =>
        {
            if (a.Count < 2) { MenuState.Cvars.Set("scoreboard_columns", MenuState.Cvars.GetString("scoreboard_columns")); return; }
            string spec = string.Join(' ', a.Skip(1));
            MenuState.Cvars.Set("scoreboard_columns", spec);
        }, "set the scoreboard's column layout: scoreboard_columns_set [default|all|<spec>]");

        // ---- the four command-PREFIX dispatchers every commands.cfg alias bottoms out in ----
        //
        // commands.cfg builds ~158 aliases of the form `alias lsmaps "qc_cmd_svcmd lsmaps ${* ?}"`, and the
        // qc_cmd_* names are themselves aliases resolving to one of four verbs (`cmd`, `sv_cmd`, `cl_cmd`,
        // `menu_cmd`) picked by the if_client/if_dedicated pair in xonotic-common.cfg. In QC each of those
        // four is a registered engine/progs command. None of them existed here — so every one of those
        // aliases expanded correctly and then died on the last hop, reaching the router as an unknown `cmd
        // <verb>` line and coming back "Unknown command". These four registrations are that last hop.
        //
        // Verified against the real cfg tree by CommandDispatchTests, which asserts what each alias actually
        // expands to rather than what this comment claims it does.

        // DP Cmd_ForwardToServer: the client→server channel. QC's CLIENT_COMMAND set (the `cmd` prefix) plus
        // everything aliased through qc_cmd_cmd/qc_cmd_svcmd — lsmaps, records, rankings, ladder, printmaplist,
        // teamstatus, info, time, cvar_changes, join, ready, vote, …
        interp.RegisterCommand("cmd", a => ForwardToGame(JoinTail(a), "cmd"),
            "send a client command to the server: cmd <command> [args]");

        // QC SERVER_COMMAND (the sv_cmd prefix): admin verbs — kick/ban/gotomap/endmatch/shuffleteams/…
        // Deliberately NOT forwarded to a remote server: the tail alone would be judged by the server's
        // client-privilege gate and rejected anyway, so a round trip would only turn an honest local message
        // into a confusing remote one.
        interp.RegisterCommand("sv_cmd", a =>
        {
            string tail = JoinTail(a);
            if (tail.Length == 0) { Print("usage: sv_cmd <command> [args]"); return; }
            if (_localRouter?.Invoke(tail) is not string outp)
            {
                Print($"sv_cmd: \"{tail}\" needs a server you are hosting (try `map <name>` first).");
                return;
            }
            string trimmed = outp.TrimEnd('\n', '\r', ' ', '\t');
            if (trimmed.Length > 0)
                Print(trimmed);
        }, "run a server admin command on the server you are hosting: sv_cmd <command> [args]");

        // QC CLIENT_COMMAND run locally (the cl_cmd prefix). Dispatches only names that are REGISTERED
        // commands: registered names outrank aliases in the interpreter, so re-entering ExecuteLine for one
        // cannot loop back through the alias that sent us here (`help` = "cl_cmd help; cmd help" is exactly
        // that shape). An unregistered name is reported rather than routed — a client command the port has
        // not implemented is not the server's business.
        interp.RegisterCommand("cl_cmd", a =>
        {
            string tail = JoinTail(a);
            if (tail.Length == 0) { Print("usage: cl_cmd <command> [args]"); return; }
            if (a.Count >= 2 && interp.CommandNames.Any(n => string.Equals(n, a[1], StringComparison.OrdinalIgnoreCase)))
                interp.ExecuteLine(tail);
            else
                Print($"cl_cmd: unknown client command \"{a[1]}\".");
        }, "run a client-side command: cl_cmd <command> [args]");

        // QC MENU_COMMAND (the menu_cmd prefix): the front-end verbs, routed through the menu's own dispatcher.
        interp.RegisterCommand("menu_cmd", a =>
        {
            string tail = JoinTail(a);
            if (tail.Length == 0) { Print("usage: menu_cmd <command> [args]"); return; }
            MenuCommand.Run(tail);
        }, "run a menu command: menu_cmd <command> [args]");

        // `lsmaps [gametype]` — QC CommonCommand_lsmaps. Registered CLIENT-side (and therefore ahead of the
        // `commands.cfg` alias, which registered commands outrank) because at the menu there is no server to
        // answer and the alias's route ends in the generic "no server — start a match first" hint. A player
        // asking what maps they have installed can be answered from the mounted search path without one.
        //
        // A live game still owns the answer: on a listen host that is this very catalog (NetGame feeds
        // MapList.LsmapsReply into the server's reply seam), and on a remote server it is THEIR pool — which
        // is the question worth asking, since `vcall gotomap` can only pick from that.
        interp.RegisterCommand("lsmaps", a =>
        {
            if (_localRouter?.Invoke("lsmaps") is string local)
            {
                string trimmed = local.TrimEnd('\n', '\r', ' ', '\t');
                if (trimmed.Length > 0)
                    Print(trimmed);
                return;
            }
            if (MenuCommand.InMatch?.Invoke() == true)
            {
                _remoteSender?.Invoke("lsmaps"); // reply arrives async via the server print channel
                return;
            }
            // No server anywhere: report what THIS install has. The optional argument is the gametype filter
            // (QC MapInfo_CheckMap); with no live gametype to inherit, the unfiltered catalog is the honest
            // default rather than a guess at what the player will end up hosting.
            Print(MapList.LsmapsReply(a.Count >= 2 ? a[1] : null));
        }, "list the installed maps, optionally for one gametype: lsmaps [gametype]");

        interp.RegisterCommand("vid_restart", _ =>
        {
            MenuCommand.VideoRestart?.Invoke();
            // vid_restart_resetrenderer (default 1): also reset the renderer, i.e. do what r_restart does.
            //
            // This exists because of a real difference between the two engines. In DarkPlaces vid_restart
            // recreates the GL context, which re-uploads every texture - so gl_picmip and
            // gl_texturecompression genuinely ARE applied by vid_restart there, and players know it. Here
            // textures are cached Godot resources that re-applying the window settings never touches, so a
            // literal port of vid_restart would silently do less than the same command does in Base.
            //
            // Default 1 keeps the Base expectation. It is a cvar because the reset is a MAP RELOAD, which is
            // far more disruptive than the resolution change that usually triggers it - set it to 0 and
            // vid_restart only touches the window, leaving r_restart as the explicit way to ask.
            if (MenuState.Cvars.GetFloat("vid_restart_resetrenderer") != 0f)
                ResetRenderer("vid_restart");
        }, "re-apply the video settings (and reset the renderer, unless vid_restart_resetrenderer is 0)");
        interp.RegisterCommand("snd_restart", _ => MenuCommand.AudioRestart?.Invoke(),
            "restart the sound system");

        // (F9) r_restart - DP's renderer restart. Here it re-hosts the CURRENT map, because that is what
        // actually re-applies the settings that are baked in at map-build time: gl_picmip and
        // gl_texturecompression are consumed as each texture is decoded, r_subdivisions_tolerance when the
        // bezier patches are tessellated, r_shadow_world_casts when the world cells are created. A
        // vid_restart cannot touch any of them - it only re-applies the window/vsync/fps family.
        interp.RegisterCommand("r_restart", _ => ResetRenderer("r_restart"),
            "reload the current map to re-apply settings that are baked in at map-build time");
        interp.RegisterCommand("togglemenu", a =>
        {
            int mode = (a.Count >= 2 && a[1] == "0") ? 0 : -1;
            MenuCommand.ToggleMenu?.Invoke(mode);
        }, "show or hide the menu");
    }

    /// <summary>Everything after the verb, rejoined — the line a prefix dispatcher passes on.</summary>
    private static string JoinTail(IReadOnlyList<string> argv)
        => argv.Count < 2 ? string.Empty : string.Join(' ', argv.Skip(1));

    /// <summary>
    /// Send <paramref name="line"/> down the same client→server route an unrouted console line takes: the
    /// in-process listen world first, then the connected server. <paramref name="verb"/> only names the
    /// dispatcher in the usage message.
    /// </summary>
    private void ForwardToGame(string line, string verb)
    {
        if (line.Length == 0)
        {
            Print($"usage: {verb} <command> [args]");
            return;
        }
        if (_localRouter?.Invoke(line) is string outp)
        {
            string trimmed = outp.TrimEnd('\n', '\r', ' ', '\t');
            if (trimmed.Length > 0)
                Print(trimmed);
            return;
        }
        // No local world: the connected server, or (at the menu) the sender's own "no server" hint.
        _remoteSender?.Invoke(line);
    }

    // =============================================================================================
    //  UI construction + styling (DP Con_DrawConsole geometry)
    // =============================================================================================

    private void BuildUi()
    {
        // The drop-down itself. ClipContents is what lets the background layers be drawn full-screen-sized with
        // their bottom edge at the console's bottom (DP's `lines - vid_conheight` origin) and simply be cut off.
        _panel = new Control { Name = "ConsolePanel", Visible = false, ClipContents = true };
        _panel.AnchorLeft = 0f; _panel.AnchorTop = 0f; _panel.AnchorRight = 1f; _panel.AnchorBottom = 0f;
        // Stop, not Pass: while the drop-down is showing it must absorb clicks that land on it rather than
        // letting them through to the menu behind. Children (the scrollback's text selection, the input line)
        // are hit-tested first, so they still work. Invisible while closed, so it blocks nothing then.
        _panel.MouseFilter = Control.MouseFilterEnum.Stop;
        AddChild(_panel);

        _background = new ConsoleBackground { Name = "ConsoleBack" };
        _background.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _background.MouseFilter = Control.MouseFilterEnum.Ignore;
        _panel.AddChild(_background);

        // Content sits inside a margin so text never touches the screen edge (DP insets by con_textsize).
        var margin = new MarginContainer { Name = "ConsoleMargin" };
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 8);
        margin.AddThemeConstantOverride("margin_right", 8);
        margin.AddThemeConstantOverride("margin_top", 4);
        margin.AddThemeConstantOverride("margin_bottom", 2);
        margin.MouseFilter = Control.MouseFilterEnum.Pass;
        _panel.AddChild(margin);

        var vbox = new VBoxContainer { Name = "ConsoleRows" };
        vbox.AddThemeConstantOverride("separation", 0);
        margin.AddChild(vbox);

        _output = new RichTextLabel
        {
            Name = "ConsoleOutput",
            BbcodeEnabled = true,
            ScrollActive = true,
            ScrollFollowing = true,
            SelectionEnabled = true,
            FocusMode = Control.FocusModeEnum.None,    // never steal focus from the input line
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        vbox.AddChild(_output);

        // DP draws the `]` as part of the edit line itself (key_line[0]); here it is its own label so the
        // LineEdit's own editing (caret motion, select-all, clipboard) never has to step around it.
        var inputRow = new HBoxContainer { Name = "ConsoleInputRow" };
        inputRow.AddThemeConstantOverride("separation", 0);
        vbox.AddChild(inputRow);

        _prompt = new Label { Name = "ConsolePrompt", Text = "]" };
        _prompt.AddThemeColorOverride("font_color", new Color(0.55f, 0.78f, 1f));
        inputRow.AddChild(_prompt);

        _input = new ConsoleLineEdit
        {
            Name = "ConsoleInput",
            PlaceholderText = "",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClearButtonEnabled = false,
        };
        // Flat: the drop-down IS the frame, so the default LineEdit box would just draw a second one.
        _input.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
        _input.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        _input.HandleKey = HandleKey;
        inputRow.AddChild(_input);

        // DP draws `engineversion` in red on the console's LAST line, below the input (Con_DrawConsole).
        _version = new Label { Name = "ConsoleVersion", Text = "VortexArena " + VortexArena.Common.BuildInfo.Version };
        _version.HorizontalAlignment = HorizontalAlignment.Right;
        _version.AddThemeColorOverride("font_color", new Color(1f, 0.25f, 0.25f, 0.85f));
        _version.MouseFilter = Control.MouseFilterEnum.Ignore;
        vbox.AddChild(_version);
    }

    public override void _Process(double delta)
    {
        // Deliberately BEFORE the early-out so a closed console still proves it costs nothing, instead of being
        // an unscoped node that silently lands in proc:other either way.
        using var _scope = VortexArena.Game.Client.FrameProfiler.Scope("console");
        if (!IsOpen)
            return;
        RefreshStyle(force: false);
        // The conback layers scroll (scr_conscroll*), so while the console is up the background repaints every
        // frame. Closed, nothing here runs at all.
        _background.Advance(delta);
    }

    /// <summary>
    /// Re-resolve the console's cvar-driven geometry: <c>scr_conheight</c> (drop-down height as a fraction of
    /// the screen) and <c>con_textsize</c> (text size in the <c>vid_conheight</c> virtual canvas, scaled to the
    /// real viewport so it reads the same at any resolution — DP's virtual 2D system). Only touches the theme
    /// when a value actually changed, so this is free on a steady frame.
    /// </summary>
    private void RefreshStyle(bool force)
    {
        Vector2 vp = GetViewport().GetVisibleRect().Size;
        if (vp.X <= 0f || vp.Y <= 0f)
            return;

        float heightFrac = CvarOr("scr_conheight", 0.5f);
        // DP clamps the console to the screen; a 0 fraction is the autoscreenshot alias hiding it entirely.
        float height = Mathf.Round(vp.Y * Mathf.Clamp(heightFrac, 0f, 1f));

        float virtualHeight = Mathf.Max(CvarOr("vid_conheight", 600f), 1f);
        float textSize = Mathf.Max(CvarOr("con_textsize", 8f), 1f);
        int fontPx = (int)Mathf.Clamp(Mathf.Round(textSize * vp.Y / virtualHeight), 8f, 64f);

        if (!force && fontPx == _styledFontPx && Mathf.IsEqualApprox(height, _styledHeight) && vp == _styledViewport)
            return;
        _styledFontPx = fontPx;
        _styledHeight = height;
        _styledViewport = vp;

        _panel.OffsetBottom = height;

        FontFile? mono = ConsoleFont;
        ApplyFont(_output, "normal_font", "normal_font_size", mono, fontPx);
        ApplyFont(_input, "font", "font_size", mono, fontPx);
        ApplyFont(_prompt, "font", "font_size", mono, fontPx);
        // The version line is deliberately smaller — it is a watermark, not content.
        ApplyFont(_version, "font", "font_size", mono, Math.Max(8, fontPx - 2));

        // Tell the completion engine how wide the console is in characters, so its packed columns (DP
        // Con_DisplayList) wrap where the text actually wraps.
        if (_commands is not null)
        {
            float charWidth = MeasureCharWidth(mono, fontPx);
            _commands.Completion.LineWidth = charWidth > 0.5f
                ? Math.Clamp((int)((vp.X - 16f) / charWidth), 20, 400)
                : 80;
        }
    }

    private static void ApplyFont(Control c, string fontName, string sizeName, FontFile? font, int px)
    {
        if (font is not null)
            c.AddThemeFontOverride(fontName, font);
        c.AddThemeFontSizeOverride(sizeName, px);
    }

    /// <summary>Width of one character in the console face — the columns are laid out in character cells, so a
    /// proportional fallback font would misalign them. Falls back to a 0.6 em estimate.</summary>
    private static float MeasureCharWidth(FontFile? font, int px)
    {
        if (font is null)
            return px * 0.6f;
        return font.GetStringSize("0", HorizontalAlignment.Left, -1, px).X;
    }

    /// <summary>
    /// The console face: DejaVu Sans Mono, which Xonotic ships in <c>font-dejavu.pk3dir</c> and DP uses for
    /// <c>FONT_CONSOLE</c>. Monospace matters here — the completion lists and <c>cvarlist</c> output are laid
    /// out in character columns. Null (no content tree mounted) falls back to Godot's default face, which still
    /// works, just without aligned columns.
    /// </summary>
    private static FontFile? ConsoleFont => _consoleFont ??= MenuState.SharedAssets?.GetFont("dejavusansmono");

    private static FontFile? _consoleFont;

    private float CvarOr(string name, float fallback)
    {
        if (_cvars is null || !_cvars.Has(name))
            return fallback;
        string raw = _cvars.GetString(name);
        return string.IsNullOrWhiteSpace(raw) ? fallback : _cvars.GetFloat(name);
    }

    // =============================================================================================
    //  open / close
    // =============================================================================================

    public override void _Input(InputEvent @event)
    {
        // Backtick toggles the console anywhere — consume it so it doesn't open the pause menu or leak into
        // gameplay. Closing obeys DP's `con_closeontoggleconsole` (keys.c:28): 0 = the key only ever opens
        // (Escape closes), 1 = it closes only with the caret at the start of the line, 2+ = anywhere. The point
        // of 1 (the default) is that a backtick typed mid-line is a CHARACTER, so an alias body or a say line
        // can actually contain one — which was impossible while this unconditionally toggled.
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Quoteleft })
        {
            if (!IsOpen)
            {
                Open();
                GetViewport().SetInputAsHandled();
                return;
            }
            int mode = _cvars is null ? 1 : (int)CvarOr("con_closeontoggleconsole", 1f);
            if (mode >= 2 || (mode == 1 && _input.CaretColumn == 0))
            {
                Close();
                GetViewport().SetInputAsHandled();
            }
            return;   // mode 0, or mid-line at mode 1: fall through and let the line edit type the character
        }

        // Escape release: if we previously closed the console on its matching press, swallow the release too —
        // Shell's pause-menu toggle fires on the Escape RELEASE edge (its design — see Shell._UnhandledKeyInput
        // comment about mouse-capture swallowing the press), so leaking the release would pop the pause menu the
        // instant the console closes. The press handler below set _eatEscapeRelease; clear it on the way out.
        if (_eatEscapeRelease && @event is InputEventKey { Pressed: false, Echo: false, Keycode: Key.Escape })
        {
            _eatEscapeRelease = false;
            GetViewport().SetInputAsHandled();
            return;
        }

        if (!IsOpen)
            return;

        // While open, Escape closes the console (instead of opening the pause menu — Shell never sees it).
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            Close();
            _eatEscapeRelease = true;        // consume the matching release so Shell's release-edge toggle no-ops
            GetViewport().SetInputAsHandled();
            return;
        }

        // DP Key_Console's wheel rules. The UNMODIFIED wheel is deliberately left to the scrollback widget's own
        // smooth scrolling — it already does the right thing, and fighting it would only make it worse. Ctrl
        // (one line) and Shift (a quarter page) are the DP-specific steps the widget has no equivalent for.
        if (@event is InputEventMouseButton { Pressed: true } mb
            && mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            bool up = mb.ButtonIndex == MouseButton.WheelUp;
            if (mb.CtrlPressed)
            {
                ScrollLines(up ? -1 : 1);
                GetViewport().SetInputAsHandled();
            }
            else if (mb.ShiftPressed)
            {
                ScrollLines(up ? -QuarterPageLines() : QuarterPageLines());
                GetViewport().SetInputAsHandled();
            }
        }
    }

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void Open()
    {
        if (IsOpen)
            return;
        _panel.Visible = true;
        ConsoleState.IsOpen = true;               // freezes the play path's polled input + fires release-all
        MouseCapture.SetWantCapture(false);        // free the cursor for typing (focus-gated in MouseCapture)
        _input.Clear();
        RefreshStyle(force: true);
        _output.ScrollToLine(Math.Max(0, _output.GetLineCount() - 1));
        _input.GrabFocus();
    }

    public void Close()
    {
        if (!IsOpen)
            return;
        _panel.Visible = false;
        ConsoleState.IsOpen = false;
        _input.ReleaseFocus();
        // Want the cursor recaptured only if a match is live and not paused; otherwise leave it free (menu).
        // MouseCapture only actually grabs while the window is focused.
        MouseCapture.SetWantCapture(_shouldCaptureOnClose?.Invoke() ?? false);
    }

    // =============================================================================================
    //  output
    // =============================================================================================

    /// <summary>Append a line of command/server output (may carry Quake <c>^</c> colour codes). Routes through
    /// the Log facade at HELP level so the line lands in the buffer + the editor Output panel WITHOUT a
    /// <c>[::client::INFO]</c> header (HELP is bare-message at every dev level — DP's <c>LOG_HELP</c>) — typed
    /// console replies shouldn't disappear from the scrollback the next time the user reopens the console, and
    /// shouldn't gain a header at developer 1+.</summary>
    public void Print(string line) => Log.Help(line ?? "");

    /// <summary>Clear the visible scrollback (the <c>clear</c> command). The Log buffer is preserved so
    /// reopening the console after `clear` still shows the history — matches DP's <c>con_clear</c> which
    /// scrolls past, not erases the journal.</summary>
    public void Clear() => _output.Clear();

    /// <summary>Live handler installed on <see cref="Log.EntryRecorded"/>: render the entry at the current dev
    /// level if it would be visible. Deferred because logs may be emitted off the main thread / mid-frame.</summary>
    private void OnLogEntry(LogEntry entry)
    {
        Callable.From(() =>
        {
            int dev = CurrentDeveloper();
            if (!Log.IsVisibleAt(entry.Level, dev))
                return;
            string? rendered = Log.Render(entry, dev);
            if (rendered is null)
                return;
            AppendBuffer(Log.ToBBCode(entry.Level, rendered));
        }).CallDeferred();
    }

    /// <summary>Re-render the entire scrollback from the Log ring buffer at the current <c>developer</c>
    /// level. Called on _Ready, after Initialize wires the live cvar, and whenever `developer` changes —
    /// switching from 0 → 1 reveals previously buffered Trace lines; switching back hides them.</summary>
    private void RebuildScrollback()
    {
        if (_output == null || !GodotObject.IsInstanceValid(_output))
            return;
        int dev = CurrentDeveloper();
        if (dev == _renderedDeveloper && _output.GetParagraphCount() > 0)
            return; // nothing to do — already at this level and scrollback isn't empty
        _output.Clear();
        AppendBuffer("[color=#888888]VortexArena console. [/color][color=#cccccc]help[/color]" +
                     "[color=#888888] for a hint, [/color][color=#cccccc]search <words>[/color]" +
                     "[color=#888888] to find a setting, [/color][color=#cccccc]`[/color]" +
                     "[color=#888888] to close. developer = [/color][color=#cccccc]" + dev.ToString() +
                     "[color=#888888].[/color]");
        foreach (LogEntry e in Log.BufferSnapshot())
        {
            if (!Log.IsVisibleAt(e.Level, dev))
                continue;
            string? rendered = Log.Render(e, dev);
            if (rendered is null)
                continue;
            AppendBuffer(Log.ToBBCode(e.Level, rendered));
        }
        _renderedDeveloper = dev;
    }

    /// <summary>The live <c>developer</c> level (0 when no cvar store is wired yet — early in _Ready).</summary>
    private int CurrentDeveloper() => _cvars is null ? 0 : (int)_cvars.GetFloat("developer");

    /// <summary>The scrollback as plain text with its Quake <c>^</c> colour codes intact — what <c>condump</c>
    /// writes. Same source and same developer-level filter the visible scrollback is rendered from, so the file
    /// matches what is on screen.</summary>
    private string BuildScrollbackText()
    {
        int dev = CurrentDeveloper();
        var sb = new System.Text.StringBuilder();
        foreach (LogEntry e in Log.BufferSnapshot())
        {
            if (!Log.IsVisibleAt(e.Level, dev))
                continue;
            if (Log.Render(e, dev) is string rendered)
                sb.Append(rendered).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Append one already-BBCode-formatted line, trimming the oldest paragraphs past the cap.</summary>
    private void AppendBuffer(string bbcode)
    {
        if (_output == null || !GodotObject.IsInstanceValid(_output))
            return; // a deferred log line raced node teardown
        _output.AppendText(bbcode + "\n");
        while (_output.GetParagraphCount() > MaxParagraphs)
            _output.RemoveParagraph(0);
    }

    // =============================================================================================
    //  scrollback paging (DP con_backscroll)
    // =============================================================================================

    /// <summary>How many text lines the scrollback currently shows.</summary>
    private int VisibleLines()
        => Math.Max(1, (int)(_output.Size.Y / Math.Max(1, _styledFontPx + 2)));

    /// <summary>DP's half-page PgUp/PgDn step, in text lines.</summary>
    private int HalfPageLines() => Math.Max(1, VisibleLines() / 2);

    /// <summary>DP's quarter-page Ctrl+PgUp/PgDn (and Shift+wheel) step, in text lines.</summary>
    private int QuarterPageLines() => Math.Max(1, VisibleLines() / 4);

    /// <summary>Scroll the scrollback by <paramref name="lines"/> (negative = toward older text).</summary>
    private void ScrollLines(int lines)
    {
        VScrollBar? bar = _output.GetVScrollBar();
        if (bar is null)
            return;
        // Scrolling away from the bottom must stop the auto-follow, or the next printed line yanks the view back.
        _output.ScrollFollowing = false;
        bar.Value += lines * (_styledFontPx + 2);
        if (bar.Value >= bar.MaxValue - bar.Page - 1.0)
            _output.ScrollFollowing = true;    // back at the bottom: resume following new output
    }

    /// <summary>DP Ctrl+Home / Ctrl+End: jump to the oldest / newest console text.</summary>
    private void ScrollToEnd(bool top)
    {
        VScrollBar? bar = _output.GetVScrollBar();
        if (bar is null)
            return;
        if (top)
        {
            _output.ScrollFollowing = false;
            bar.Value = bar.MinValue;
        }
        else
        {
            bar.Value = bar.MaxValue;
            _output.ScrollFollowing = true;
        }
    }

    // =============================================================================================
    //  key handling (DP Key_Console + Key_Parse_CommonKeys)
    // =============================================================================================

    /// <summary>
    /// The console's key map, called from the focused input line for every key press. Returns true when the key
    /// was consumed (so Godot's <see cref="LineEdit"/> never also sees it).
    ///
    /// <para>Only the keys DP has AND Godot's LineEdit does not already implement are handled here. Word-wise
    /// caret motion (Ctrl+Left/Right), Home/End, Ctrl+Backspace/Delete, select-all, copy/cut and undo are
    /// native LineEdit behaviour that matches <c>Key_Parse_CommonKeys</c> closely enough to leave alone.
    /// DP's Insert-key overwrite mode has no LineEdit equivalent and is not reproduced.</para>
    /// </summary>
    private bool HandleKey(InputEventKey k)
    {
        bool ctrl = k.CtrlPressed, shift = k.ShiftPressed, alt = k.AltPressed;
        // DP forbids Ctrl+Alt shortcuts outright: on several non-English layouts AltGr emulates Ctrl+Alt, so
        // claiming those combos would eat the characters they type.
        if (ctrl && alt)
            return false;
        bool plain = !ctrl && !shift && !alt;

        switch (k.Keycode)
        {
            // ---- submit ----
            case Key.Enter or Key.KpEnter when !ctrl && !alt:
                Submit(_input.Text);
                return true;

            // ---- history ----
            case Key.Up when plain:
            case Key.P when ctrl:
                Fetch(_history.Up(_input.Text));
                return true;
            case Key.Down when plain:
            case Key.N when ctrl:
                Fetch(_history.Down());
                return true;
            case Key.Comma when ctrl:
                Fetch(_history.First(_input.Text));
                return true;
            case Key.Period when ctrl:
                Fetch(_history.Last(_input.Text));
                return true;

            // ---- history search (DP Ctrl+R / Ctrl+Shift+R / Ctrl+F) ----
            // These POINT at a match and echo it without fetching, so repeating the key keeps walking; the next
            // Up/Down is what pulls the found line into the edit line.
            case Key.R when ctrl && shift:
                EchoHistoryMatch(_history.FindForwards(_input.Text));
                return true;
            case Key.R when ctrl:
                EchoHistoryMatch(_history.FindBackwards(_input.Text));
                return true;
            case Key.F when ctrl:
                PrintHistoryMatches(_input.Text);
                return true;

            // ---- completion ----
            case Key.Tab when ctrl:
                Apply(_commands?.Completion.AppendCvarValue(_input.Text, _input.CaretColumn));
                return true;
            case Key.Tab when !alt:
                Apply(_commands?.Completion.Complete(_input.Text, _input.CaretColumn));
                return true;

            // ---- line editing DP has and LineEdit does not ----
            case Key.U when ctrl:                       // vi/readline ^u: discard the line
                SetInputText("");
                return true;
            case Key.Q when ctrl:                       // zsh ^q: park the line in history without running it
                _history.Push(_input.Text);
                SetInputText("");
                return true;
            case Key.L when ctrl:                       // readline ^l: clear the screen
                Clear();
                return true;
            case Key.H when ctrl:                       // readline ^h: backspace
                Backspace();
                return true;
            case Key.V when ctrl:                       // DP's paste, which folds newlines into `; ` separators
                PasteClipboard();
                return true;

            // ---- scrollback ----
            case Key.Pageup when ctrl:
                ScrollLines(-QuarterPageLines());
                return true;
            case Key.Pageup when plain:
                ScrollLines(-HalfPageLines());
                return true;
            case Key.Pagedown when ctrl:
                ScrollLines(QuarterPageLines());
                return true;
            case Key.Pagedown when plain:
                ScrollLines(HalfPageLines());
                return true;
            case Key.Home when ctrl:
                ScrollToEnd(top: true);
                return true;
            case Key.End when ctrl:
                ScrollToEnd(top: false);
                return true;

            // ---- text zoom (DP Ctrl+= / Ctrl+- / Ctrl+0 on con_textsize) ----
            case Key.Equal or Key.Plus or Key.KpAdd when ctrl:
                ZoomText(+1);
                return true;
            case Key.Minus or Key.KpSubtract when ctrl:
                ZoomText(-1);
                return true;
            case Key.Key0 when ctrl:
                ResetTextSize();
                return true;
        }
        return false;
    }

    /// <summary>Run the typed line: echo it as DP does (<c>]command</c>), record it, and interpret it.</summary>
    private void Submit(string text)
    {
        _input.Clear();
        if (string.IsNullOrWhiteSpace(text))
        {
            _history.Push("");        // resets the history cursor to "editing a fresh line"
            return;
        }

        // DP Key_History_Push echoes the submitted line into the console buffer, so it survives a scrollback
        // rebuild and shows up in `condump` — hence Print (the Log buffer) rather than a direct append.
        Print("^5]" + text);
        _history.Push(text);

        if (_interp == null)
        {
            Print("console not initialised");
            return;
        }
        try
        {
            _interp.ExecuteLine(text);
        }
        catch (Exception ex)
        {
            Log.Severe($"error: {ex.Message}");
        }
    }

    /// <summary>Put a history line into the input (null = the history had nothing to move to).</summary>
    private void Fetch(string? line)
    {
        if (line is not null)
            SetInputText(line);
    }

    /// <summary>Echo a history search hit the way DP does: green index, then the line — without fetching it.</summary>
    private void EchoHistoryMatch((int Index, string Line)? hit)
    {
        if (hit is not { } h)
            return;
        Print($"^2{h.Index + 1}^7 {h.Line}");
    }

    /// <summary>DP Ctrl+F: list every history line matching what is typed so far, the current one highlighted.</summary>
    private void PrintHistoryMatches(string partial)
    {
        Print($"History commands containing \"{partial}\":");
        var matches = _history.FindAll(partial);
        foreach ((int index, string line, bool current) in matches)
            Print($"{(current ? "^2" : "^3")}{index}^7 {line}");
        Print($"{matches.Count} result{(matches.Count == 1 ? "" : "s")}");
    }

    /// <summary>Apply a completion outcome: print whatever it wants shown, then take its line + caret.</summary>
    private void Apply(CompletionOutcome? outcome)
    {
        if (outcome is not { } o)
            return;
        foreach (string line in o.Output)
            Print(line);
        _input.Text = o.Line;
        _input.CaretColumn = Math.Clamp(o.Caret, 0, o.Line.Length);
    }

    private void Backspace()
    {
        int caret = _input.CaretColumn;
        if (caret <= 0)
            return;
        _input.Text = _input.Text.Remove(caret - 1, 1);
        _input.CaretColumn = caret - 1;
    }

    /// <summary>
    /// DP's console paste (<c>Key_Parse_CommonKeys</c> Ctrl+V): newlines become <c>; </c> so a multi-line block
    /// copied out of a config pastes as one runnable line instead of being silently truncated at the first
    /// break, which is what a plain single-line paste would do.
    /// </summary>
    private void PasteClipboard()
    {
        string clip = DisplayServer.ClipboardGet() ?? "";
        if (clip.Length == 0)
            return;
        string flat = clip.Replace("\r\n", "; ").Replace('\n', ';').Replace('\r', ';').Replace('\t', ' ');
        int caret = Math.Clamp(_input.CaretColumn, 0, _input.Text.Length);
        _input.Text = _input.Text.Insert(caret, flat);
        _input.CaretColumn = caret + flat.Length;
    }

    /// <summary>DP Ctrl+= / Ctrl+-: step <c>con_textsize</c> within its 1..128 bounds.</summary>
    private void ZoomText(int step)
    {
        if (_cvars is null)
            return;
        int size = (int)Mathf.Clamp(CvarOr("con_textsize", 8f) + step, 1f, 128f);
        _cvars.Set("con_textsize", size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        RefreshStyle(force: true);
    }

    /// <summary>DP Ctrl+0: back to the shipped <c>con_textsize</c> default.</summary>
    private void ResetTextSize()
    {
        if (_cvars is null)
            return;
        string def = _cvars.GetDefault("con_textsize");
        _cvars.Set("con_textsize", string.IsNullOrEmpty(def) ? "8" : def);
        RefreshStyle(force: true);
    }

    private void SetInputText(string s)
    {
        _input.Text = s;
        _input.CaretColumn = s.Length;
    }

    // =============================================================================================
    //  history persistence (DP Key_History_Init / _Shutdown)
    // =============================================================================================

    private void LoadHistory()
    {
        try
        {
            if (!Godot.FileAccess.FileExists(HistoryPath))
                return;
            using Godot.FileAccess f = Godot.FileAccess.Open(HistoryPath, Godot.FileAccess.ModeFlags.Read);
            if (f is not null)
                _history.Load(f.GetAsText());
        }
        catch (Exception ex)
        {
            Log.Trace($"[console] could not read {HistoryPath}: {ex.Message}");
        }
    }

    private void SaveHistory()
    {
        if (_history.Count == 0)
            return;
        try
        {
            using Godot.FileAccess f = Godot.FileAccess.Open(HistoryPath, Godot.FileAccess.ModeFlags.Write);
            f?.StoreString(_history.Save());
        }
        catch (Exception ex)
        {
            Log.Trace($"[console] could not write {HistoryPath}: {ex.Message}");
        }
    }

    /// <summary>Write a console-produced file (currently <c>condump</c>) into the user data directory. Returns
    /// the resolved path on success, null on failure — the command reports either way.</summary>
    private static string? WriteUserFile(string name, string text)
    {
        try
        {
            string path = UserPaths.Resolve(name.Replace('\\', '/').TrimStart('/'));
            using Godot.FileAccess f = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
            if (f is null)
                return null;
            f.StoreString(text);
            return path;
        }
        catch (Exception ex)
        {
            Log.Trace($"[console] condump failed: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// The console's layered background — DP <c>Con_DrawConsole</c>'s three <c>gfx/conback*</c> passes.
///
/// <para>Each layer is the SAME size as the whole screen and is positioned so its bottom edge sits at the
/// console's bottom (DP draws at <c>y = lines - vid_conheight</c>); the drop-down clips the rest. That is why
/// Xonotic's artwork reads correctly at any <c>scr_conheight</c> — you are always looking at the bottom slice
/// of a full-screen image, never a squashed one.</para>
///
/// <para>Layers 2 and 3 scroll at their own rates over layer 1 (<c>scr_conscroll2_*</c>/<c>3_*</c>), which is
/// what gives the Xonotic console its drifting look; the scroll offset wraps, so the texture repeat mode is
/// enabled on this control. Everything is tinted by <c>scr_conbrightness</c> and faded by
/// <c>scr_conalpha × scr_conalpha{,2,3}factor</c>. With no artwork mounted it falls back to DP's flat black
/// fill at the same alpha, so the console is still readable when content is missing — DP passes
/// <c>CACHEPICFLAG_FAILONMISSING</c> for exactly that reason.</para>
/// </summary>
public partial class ConsoleBackground : Control
{
    /// <summary>DP <c>host.realtime</c> as the scroll clock. Advanced by the overlay only while the console is
    /// open, so a closed console costs nothing.</summary>
    private double _clock;

    public override void _Ready()
    {
        // The scroll offsets run past the texture bounds and must wrap rather than clamp (DP's
        // CACHEPICFLAG_NOCLAMP for the scrolling layers).
        TextureRepeat = TextureRepeatEnum.Enabled;
    }

    /// <summary>Advance the scroll clock and repaint (only called while the console is showing).</summary>
    public void Advance(double delta)
    {
        _clock += delta;
        QueueRedraw();
    }

    public override void _Draw()
    {
        Vector2 size = Size;
        if (size.X <= 0f || size.Y <= 0f)
            return;

        float baseAlpha = Cvar("scr_conalpha", 1f);
        float brightness = Cvar("scr_conbrightness", 1f);

        // Layer 1 doubles as the fallback fill: if the art is missing (or brightness is ~0), DP fills black.
        float alpha1 = baseAlpha * Cvar("scr_conalphafactor", 1f);
        if (alpha1 > 0f)
        {
            Texture2D? back = brightness >= 0.01f ? VortexArena.Game.Hud.TextureCache.Get("gfx/conback") : null;
            if (back is not null)
                DrawLayer(back, size, brightness, alpha1, Cvar("scr_conscroll_x", 0f), Cvar("scr_conscroll_y", 0f));
            else
                DrawRect(new Rect2(Vector2.Zero, size), new Color(0f, 0f, 0f, Mathf.Clamp(alpha1, 0f, 1f)));
        }

        float alpha2 = baseAlpha * Cvar("scr_conalpha2factor", 0f);
        if (alpha2 > 0f && VortexArena.Game.Hud.TextureCache.Get("gfx/conback2") is { } back2)
            DrawLayer(back2, size, brightness, alpha2, Cvar("scr_conscroll2_x", 0f), Cvar("scr_conscroll2_y", 0f));

        float alpha3 = baseAlpha * Cvar("scr_conalpha3factor", 0f);
        if (alpha3 > 0f && VortexArena.Game.Hud.TextureCache.Get("gfx/conback3") is { } back3)
            DrawLayer(back3, size, brightness, alpha3, Cvar("scr_conscroll3_x", 0f), Cvar("scr_conscroll3_y", 0f));

        // A hairline at the bottom edge separates the drop-down from the game behind it. DP gets this for free
        // from the conback artwork's own border; ours has to survive the missing-art fallback too.
        DrawRect(new Rect2(0f, size.Y - 1f, size.X, 1f), new Color(0.5f, 0.6f, 0.75f, 0.55f));
    }

    /// <summary>
    /// One conback pass: the full-screen-sized image with its BOTTOM edge at the console's bottom, its source
    /// region offset by the layer's scroll rate (wrapping, hence <see cref="TextureRepeatEnum.Enabled"/>).
    /// </summary>
    private void DrawLayer(Texture2D tex, Vector2 size, float brightness, float alpha, float scrollX, float scrollY)
    {
        Vector2 screen = GetViewportRect().Size;
        if (screen.Y <= 0f)
            screen = size;

        // DP: sx = scr_conscroll_x * realtime, then keeps only the fraction — the offset wraps once per second
        // per unit of scroll rate.
        double sx = scrollX * _clock; sx -= Math.Floor(sx);
        double sy = scrollY * _clock; sy -= Math.Floor(sy);

        Vector2 texSize = tex.GetSize();
        var src = new Rect2((float)(sx * texSize.X), (float)(sy * texSize.Y), texSize.X, texSize.Y);
        // Bottom-aligned full-screen destination (DP's `0, lines - vid_conheight, vid_conwidth, vid_conheight`).
        var dst = new Rect2(0f, size.Y - screen.Y, size.X, screen.Y);

        float b = Mathf.Clamp(brightness, 0f, 1f);
        DrawTextureRectRegion(tex, dst, src, new Color(b, b, b, Mathf.Clamp(alpha, 0f, 1f)));
    }

    private static float Cvar(string name, float fallback)
    {
        CvarService c = MenuState.Cvars;
        if (!c.Has(name))
            return fallback;
        string raw = c.GetString(name);
        return string.IsNullOrWhiteSpace(raw) ? fallback : c.GetFloat(name);
    }
}

/// <summary>
/// The console input line — a <see cref="LineEdit"/> that hands every key press to the console's own key map
/// (<see cref="HandleKey"/>) before Godot's built-in editing sees it, and consumes the ones the console claims
/// via <see cref="Control.AcceptEvent"/>.
///
/// <para>Consuming matters for more than tidiness: Tab would otherwise move focus, the arrows would only move
/// the caret, and Enter would reach the native <see cref="LineEdit"/> submit path — which drops keyboard focus
/// from the field, forcing a re-click before the next command could be typed.</para>
///
/// <para>Anything the console does NOT claim falls through to the native handling, which is deliberate: Godot's
/// LineEdit already implements most of DP's <c>Key_Parse_CommonKeys</c> (word-wise caret motion, Home/End,
/// Ctrl+Backspace/Delete, select-all, copy/cut, undo) and reimplementing it here would only be a worse copy.</para>
/// </summary>
public partial class ConsoleLineEdit : LineEdit
{
    /// <summary>The console's key map. Returns true to consume the key.</summary>
    public Func<InputEventKey, bool>? HandleKey;

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true } k)
            return;
        // Echo (auto-repeat) is allowed through for the keys where holding is natural — history stepping and
        // scrollback paging — which is what DP does; the rest are edge-triggered anyway.
        if (HandleKey?.Invoke(k) ?? false)
            AcceptEvent();
    }
}
