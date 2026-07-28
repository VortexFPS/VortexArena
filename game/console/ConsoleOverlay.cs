using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using XonoticGodot.Common.Config;
using XonoticGodot.Common.Diagnostics;
using XonoticGodot.Engine.Console;
using XonoticGodot.Engine.Simulation;
using XonoticGodot.Game.Menu;

namespace XonoticGodot.Game.Console;

/// <summary>
/// The in-game developer console overlay — the C# successor to DP's drop-down console (<c>con_*</c> +
/// <c>Key_Console</c>). A high <see cref="CanvasLayer"/> (above the menu/HUD) holding a translucent top
/// drop-down with a scrollback <see cref="RichTextLabel"/> and an input <see cref="LineEdit"/>. Backtick
/// (<c>`</c>) toggles it; typed lines run through the shared <see cref="ConfigInterpreter"/> (the same buffer
/// that loads the <c>.cfg</c> tree), so the console interprets commands EXACTLY as a config file would —
/// <c>set</c>/<c>seta</c>/<c>alias</c>/<c>exec</c>/<c>$cvar</c> + the console/cvar builtins
/// (<see cref="ConsoleCommands"/>) + gameplay commands routed to the live server.
///
/// <para>It mirrors the whole <see cref="Log"/> stream into the scrollback (so every <c>LOG_*</c> line is
/// visible like DP), supports command history (Up/Down) and Tab completion (cvars + commands + aliases), and
/// renders Quake <c>^</c> colour codes via <see cref="Log.ToBBCode"/>.</para>
///
/// <para>Input model: <see cref="_Input"/> grabs backtick (toggle) and, while open, Escape (close) — consuming
/// both so they don't reach the game / pause menu. Other keys are left to the focused input field; the play path
/// (<c>NetGame</c>) independently freezes gameplay input on <see cref="ConsoleState.IsOpen"/> (its polled WASD is
/// not stopped by event consumption). The mouse is freed while open and restored via the host's
/// <c>shouldCaptureOnClose</c> on close.</para>
/// </summary>
public partial class ConsoleOverlay : CanvasLayer
{
    private const int MaxParagraphs = 1024;

    private PanelContainer _panel = null!;
    private RichTextLabel _output = null!;
    private ConsoleLineEdit _input = null!;

    private ConfigInterpreter? _interp;
    private CvarService? _cvars;
    private ConsoleCommands? _commands;
    private Func<bool>? _shouldCaptureOnClose;

    private readonly List<string> _history = new();
    private int _histIndex = -1;                 // -1 = editing a fresh line (not navigating history)

    private Action<LogEntry>? _logSubscription;  // EntryRecorded handler we installed (detached on teardown)
    private Action<string>? _cvarChangedSub;     // CvarService.Changed handler watching `developer`
    private int _renderedDeveloper = -1;         // dev level the scrollback was last rendered at
    private bool _eatEscapeRelease;              // true between the Escape press we consumed and its matching release

    /// <summary>True while the drop-down is showing (mirrors <see cref="ConsoleState.IsOpen"/>).</summary>
    public bool IsOpen => _panel.Visible;

    public override void _Ready()
    {
        Layer = 128;                              // above the menu (10), HUD (5), connect overlay (100)
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
        _commands = new ConsoleCommands(interp, cvars, Print, Clear, localRouter, remoteSender);
        RegisterHostCommands(interp);

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
    }

    /// <summary>Engine/host actions that need the Godot front-end (DP engine commands the console exposes). Wired
    /// here (not in the Godot-free <see cref="ConsoleCommands"/>) through the menu's existing host hooks.</summary>
    private void RegisterHostCommands(ConfigInterpreter interp)
    {
        interp.RegisterCommand("quit", _ => MenuCommand.Quit?.Invoke());
        interp.RegisterCommand("exit", _ => MenuCommand.Quit?.Invoke());
        interp.RegisterCommand("disconnect", _ => MenuCommand.Disconnect?.Invoke());
        interp.RegisterCommand("connect", a =>
        {
            if (a.Count >= 2) MenuCommand.Connect?.Invoke(a[1]);
            else Print("usage: connect <address>");
        });
        interp.RegisterCommand("map", a =>
        {
            if (a.Count >= 2) MenuCommand.StartMap?.Invoke(a[1]);
            else Print("usage: map <name>");
        });
        interp.RegisterCommand("devmap", a => { if (a.Count >= 2) MenuCommand.StartMap?.Invoke(a[1]); });
        // QC Cmd_Scoreboard_SetFields (client/hud/panel/scoreboard.qc:767), exposed in Base as the
        // `scoreboard_columns_set` client command: set the scoreboard's column layout. No argument re-applies the
        // saved `scoreboard_columns`; "default" / "all" select the two presets; anything else is a literal spec
        // ("ping pl name | score deaths"). The scoreboard reads the cvar live, so writing it IS the command.
        interp.RegisterCommand("scoreboard_columns_set", a =>
        {
            if (a.Count < 2) { MenuState.Cvars.Set("scoreboard_columns", MenuState.Cvars.GetString("scoreboard_columns")); return; }
            string spec = string.Join(' ', a.Skip(1));
            MenuState.Cvars.Set("scoreboard_columns", spec);
        });

        interp.RegisterCommand("vid_restart", _ => MenuCommand.VideoRestart?.Invoke());
        interp.RegisterCommand("snd_restart", _ => MenuCommand.AudioRestart?.Invoke());
        interp.RegisterCommand("togglemenu", a =>
        {
            int mode = (a.Count >= 2 && a[1] == "0") ? 0 : -1;
            MenuCommand.ToggleMenu?.Invoke(mode);
        });
    }

    // =============================================================================================
    //  UI construction
    // =============================================================================================

    private void BuildUi()
    {
        _panel = new PanelContainer { Name = "ConsolePanel", Visible = false };
        // Top half of the screen, full width, auto-resizing with the window.
        _panel.AnchorLeft = 0f; _panel.AnchorTop = 0f; _panel.AnchorRight = 1f; _panel.AnchorBottom = 0.5f;
        _panel.OffsetLeft = 0f; _panel.OffsetTop = 0f; _panel.OffsetRight = 0f; _panel.OffsetBottom = 0f;
        var bg = new StyleBoxFlat { BgColor = new Color(0.04f, 0.04f, 0.06f, 0.92f) };
        bg.ContentMarginLeft = bg.ContentMarginRight = 8f;
        bg.ContentMarginTop = bg.ContentMarginBottom = 6f;
        _panel.AddThemeStyleboxOverride("panel", bg);
        AddChild(_panel);

        var vbox = new VBoxContainer();
        _panel.AddChild(vbox);

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

        _input = new ConsoleLineEdit
        {
            Name = "ConsoleInput",
            PlaceholderText = "",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ClearButtonEnabled = false,
        };
        _input.Submit = OnSubmit;
        _input.HistoryPrev = HistoryPrev;
        _input.HistoryNext = HistoryNext;
        _input.CompleteTab = OnTab;
        vbox.AddChild(_input);
    }

    // =============================================================================================
    //  open / close
    // =============================================================================================

    public override void _Input(InputEvent @event)
    {
        // Backtick toggles the console anywhere — consume it so it doesn't type a `, open the pause menu, or
        // leak into gameplay.
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Quoteleft })
        {
            Toggle();
            GetViewport().SetInputAsHandled();
            return;
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
        _histIndex = -1;
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
        AppendBuffer("[color=#888888]XonoticGodot console. Type [/color][color=#cccccc]help[/color][color=#888888] for a hint, [/color][color=#cccccc]`[/color][color=#888888] to close. developer = [/color][color=#cccccc]" + dev.ToString() + "[/color][color=#888888].[/color]");
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
    //  submit / history / completion
    // =============================================================================================

    private void OnSubmit(string text)
    {
        _input.Clear();
        _histIndex = -1;
        if (string.IsNullOrWhiteSpace(text))
            return;

        AppendBuffer($"[color=#7faaff]]{EscapeBb(text)}[/color]"); // DP echoes the typed line as ]command
        PushHistory(text);

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
            AppendBuffer($"[color=#ff5555]error: {EscapeBb(ex.Message)}[/color]");
        }
    }

    private void PushHistory(string line)
    {
        if (_history.Count == 0 || !string.Equals(_history[^1], line, StringComparison.Ordinal))
            _history.Add(line);
        if (_history.Count > 256)
            _history.RemoveAt(0);
    }

    private void HistoryPrev()
    {
        if (_history.Count == 0)
            return;
        _histIndex = Math.Max(0, (_histIndex < 0 ? _history.Count : _histIndex) - 1);
        SetInputText(_history[_histIndex]);
    }

    private void HistoryNext()
    {
        if (_history.Count == 0 || _histIndex < 0)
            return;
        _histIndex++;
        if (_histIndex >= _history.Count)
        {
            _histIndex = -1;
            SetInputText("");
        }
        else
        {
            SetInputText(_history[_histIndex]);
        }
    }

    private bool OnTab()
    {
        if (_commands == null)
            return false;
        string text = _input.Text;
        int lastSpace = text.LastIndexOf(' ');
        string head = lastSpace < 0 ? "" : text.Substring(0, lastSpace + 1);
        string token = lastSpace < 0 ? text : text.Substring(lastSpace + 1);
        if (token.Length == 0)
            return true;

        CompletionResult r = ConsoleCommands.Complete(token, _commands.CompletionNames());
        if (r.Matches.Count == 0)
            return true;
        if (r.Matches.Count == 1)
        {
            SetInputText(head + r.Completed + " ");
            return true;
        }
        SetInputText(head + r.Completed);
        AppendBuffer(string.Join("  ", r.Matches.Take(48).Select(EscapeBb)));
        if (r.Matches.Count > 48)
            AppendBuffer($"[color=#888888]…and {r.Matches.Count - 48} more[/color]");
        return true;
    }

    private void SetInputText(string s)
    {
        _input.Text = s;
        _input.CaretColumn = s.Length;
    }

    /// <summary>Escape literal <c>[</c> so user text / command echoes aren't parsed as BBCode tags.</summary>
    private static string EscapeBb(string s) => s.IndexOf('[') < 0 ? s : s.Replace("[", "[lb]");
}

/// <summary>
/// The console input line — a <see cref="LineEdit"/> that intercepts Up/Down (history), Tab (completion), and
/// Enter (submit). All are handled in <see cref="_GuiInput"/> with <see cref="Control.AcceptEvent"/> so Tab
/// doesn't move focus, the arrows don't just move the caret, and — crucially — Enter never reaches the native
/// <see cref="LineEdit"/> submit path, which drops keyboard focus from the line (forcing a re-click). We drive
/// the submit ourselves via <see cref="Submit"/> and the field keeps focus.
/// </summary>
public partial class ConsoleLineEdit : LineEdit
{
    public Action? HistoryPrev;
    public Action? HistoryNext;
    public Func<bool>? CompleteTab;
    public Action<string>? Submit;

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } k)
            return;
        switch (k.Keycode)
        {
            case Key.Up:
                HistoryPrev?.Invoke();
                AcceptEvent();
                break;
            case Key.Down:
                HistoryNext?.Invoke();
                AcceptEvent();
                break;
            case Key.Tab:
                if (CompleteTab?.Invoke() ?? false)
                    AcceptEvent();
                break;
            case Key.Enter:
            case Key.KpEnter:
                // Consume Enter so the built-in LineEdit submit (which releases focus) never runs; we submit
                // ourselves, leaving focus on the field so the next command can be typed without a re-click.
                Submit?.Invoke(Text);
                AcceptEvent();
                break;
        }
    }
}
