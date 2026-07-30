using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using VortexArena.Common.Config;
using VortexArena.Engine.Console;
using VortexArena.Game.Menu;

namespace VortexArena.Game.Console;

/// <summary>
/// The Godot input glue for the keybind system + the <c>bind</c>/<c>unbind</c>/<c>unbindall</c> sink that the
/// canonical <c>binds-xonotic.cfg</c> populates the runtime table from. It turns a Godot
/// <see cref="InputEvent"/> into the canonical key string the (Godot-free) <see cref="BindTable"/> understands
/// and, conversely, translates the engine key names the stock config uses (<c>UPARROW</c>, <c>SHIFT</c>,
/// <c>SPACE</c>, <c>MOUSE1</c>, <c>MWHEELUP</c>, <c>KP_*</c>, …) into those same canonical strings so a bind
/// from the cfg resolves against a live key event. The C# successor to the engine's <c>Key_Event</c> →
/// <c>Key_SetBinding</c> plumbing (keys.c); the pure press/release button state lives in <see cref="BindTable"/>.
///
/// <para><b>The linchpin (DP <c>bind</c> command).</b> <see cref="RegisterBindCommands"/> registers the
/// <c>bind</c>/<c>unbind</c>/<c>unbindall</c> handlers on a <see cref="ConfigInterpreter"/> so that when
/// <c>xonotic-client.cfg</c> execs <c>binds-xonotic.cfg</c> (xonotic-client.cfg:603) the full canonical bind
/// set lands straight in <see cref="BindTable"/> — the single source of truth both gameplay input paths read.
/// (Engine commands like <c>bind</c> are on the interpreter's NonCvarCommands denylist, so they don't become
/// junk cvars; the registered handler is consulted before the cvar/alias fallback.)</para>
///
/// One-time <see cref="Install"/> hooks the console-open signal to drop all held buttons (DP
/// <c>in_releaseall</c>) so a key held when the console opens doesn't stick down afterwards.
/// </summary>
public static class BindInput
{
    private static bool _installed;

    /// <summary>The <see cref="KeyBindings"/> action ids → the DP command each drives, the C# successor to the
    /// keybinder.qc action vocabulary (KeyBinds_BuildList). Movement/fire/zoom/scores map to the held
    /// <c>+</c>-commands; weapon-select + the rest to their one-shot console command.</summary>
    private static readonly Dictionary<string, string> ActionToCommand = new(StringComparer.OrdinalIgnoreCase)
    {
        // movement (held +commands)
        ["forward"] = "+forward",
        ["back"] = "+back",
        ["left"] = "+moveleft",
        ["right"] = "+moveright",
        ["jump"] = "+jump",
        ["crouch"] = "+crouch",
        ["hook"] = "+hook",
        ["jetpack"] = "+jetpack",
        // attacking (held)
        ["attack"] = "+fire",
        ["attack2"] = "+fire2",
        // weapons (one-shots)
        ["weapnext"] = "weapnext",
        ["weapprev"] = "weapprev",
        ["weaplast"] = "weapon_last",
        ["weapbest"] = "weapon_best",
        ["reload"] = "weapon_reload",
        ["dropweapon"] = "weapon_drop",
        // view
        ["zoom"] = "+zoom",
        ["togglezoom"] = "togglezoom",
        ["scoreboard"] = "+showscores",
        ["screenshot"] = "screenshot",
        // communication / misc (one-shots)
        ["chat"] = "messagemode",
        ["chat_team"] = "messagemode2",
        ["use"] = "+use",
        ["kill"] = "kill",
        ["console"] = "toggleconsole",
        ["quickmenu"] = "quickmenu",
    };

    /// <summary>Wire the one-time hooks (idempotent): release all held buttons whenever the console opens.</summary>
    public static void Install()
    {
        if (_installed)
            return;
        _installed = true;
        ConsoleState.Opened += BindTable.ReleaseAll;
    }

    // =====================================================================================================
    //  bind sink (DP keys.c bind_f / unbind_f / unbindall) — fed by binds-xonotic.cfg at boot
    // =====================================================================================================

    /// <summary>
    /// Register the DP <c>bind</c>/<c>unbind</c>/<c>unbindall</c> commands on <paramref name="interp"/> so that
    /// the canonical <c>binds-xonotic.cfg</c> (exec'd by <c>xonotic-client.cfg</c>) populates the runtime
    /// <see cref="BindTable"/>. Each handler translates the stock <em>engine</em> key name (<c>UPARROW</c>,
    /// <c>MOUSE1</c>, <c>SHIFT</c>, …) into the canonical key string a live <see cref="InputEvent"/> encodes to,
    /// so a cfg bind resolves against a real press. Idempotent: <see cref="ConfigInterpreter.RegisterCommand"/>
    /// overwrites by name, so registering twice (e.g. the console's own <c>ConsoleCommands</c> later) is harmless
    /// — both feed the same table.
    /// </summary>
    public static void RegisterBindCommands(ConfigInterpreter interp)
    {
        if (interp is null)
            return;
        interp.RegisterCommand("bind", argv =>
        {
            if (argv.Count < 2)
                return;
            string? key = TranslateEngineKey(argv[1]);
            if (key is null)
                return; // an unmappable key (e.g. JOY*) — silently skip rather than store an unusable bind
            // join the tail so both `bind x "+forward"` (one quoted token) and `bind x say hi` (many) work.
            BindTable.Bind(key, JoinTail(argv, 2));
        });
        interp.RegisterCommand("unbind", argv =>
        {
            if (argv.Count < 2) return;
            string? key = TranslateEngineKey(argv[1]);
            if (key is not null) BindTable.Unbind(key);
        });
        interp.RegisterCommand("unbindall", _ => BindTable.UnbindAll());
    }

    // =====================================================================================================
    //  menu key binder (keybinder.qc: edit the bind table, persist via the config dump)
    // =====================================================================================================

    /// <summary>
    /// Bind a captured key string to a <see cref="KeyBindings"/> action — the menu key binder's capture path
    /// (keybinder.qc <c>XonoticKeyBinder_keyGrabbed</c> → <c>bind KEY "func"</c>). Drops the action's previous
    /// key (one key per action in this simplified binder) so re-binding moves the bind rather than duplicating
    /// it. No-op for an unknown action or an empty key. The cvar/config dump (<see cref="WriteBindings"/>)
    /// persists the result, so the menu no longer needs the legacy <see cref="MenuSettings"/> store.
    /// </summary>
    public static void BindAction(string actionId, string key)
    {
        if (string.IsNullOrEmpty(key) || !ActionToCommand.TryGetValue(actionId, out string? cmd))
            return;
        UnbindAction(actionId); // a clean re-map: clear the old key for this action first
        BindTable.Bind(key, cmd);
    }

    /// <summary>Clear every key currently bound to an action's command (keybinder.qc Clear / re-bind). </summary>
    public static void UnbindAction(string actionId)
    {
        if (!ActionToCommand.TryGetValue(actionId, out string? cmd))
            return;
        foreach (string key in KeysForCommand(cmd))
            BindTable.Unbind(key);
    }

    /// <summary>The current key bound to an action's command, or "" if none — drives the menu row's face
    /// (keybinder.qc <c>findkeysforcommand</c>). Returns the first match (this binder keeps one key per action).</summary>
    public static string KeyForAction(string actionId)
    {
        if (!ActionToCommand.TryGetValue(actionId, out string? cmd))
            return "";
        foreach (string key in KeysForCommand(cmd))
            return key;
        return "";
    }

    /// <summary>Every key bound to <paramref name="command"/> (DP <c>findkeysforcommand</c>).</summary>
    private static IEnumerable<string> KeysForCommand(string command)
    {
        foreach (KeyValuePair<string, string> kv in BindTable.List())
            if (string.Equals(kv.Value, command, StringComparison.OrdinalIgnoreCase))
                yield return kv.Key;
    }

    // =====================================================================================================
    //  persistence (DP keys.c Key_WriteBindings — dump `bind KEY "cmd"` lines into the user config)
    // =====================================================================================================

    /// <summary>
    /// Append the runtime bind table to <paramref name="sb"/> as <c>bind "KEY" "command"</c> lines — the C#
    /// successor to DP's <c>Key_WriteBindings</c> (the bind block in config.cfg). The menu's config save
    /// (<see cref="MenuState.SaveUserConfig"/>) emits these alongside the archived <c>seta</c> cvars, and the
    /// boot loader re-runs them through the same bind sink, so a user's rebinds survive a restart without the
    /// legacy <c>user://settings.cfg</c>. Keys are emitted verbatim (already canonical), which the
    /// <see cref="TranslateEngineKey"/> identity branch round-trips on reload.
    /// </summary>
    public static void WriteBindings(StringBuilder sb)
    {
        foreach (KeyValuePair<string, string> kv in BindTable.List())
            sb.Append($"bind \"{Escape(kv.Key)}\" \"{Escape(kv.Value)}\"\n");
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // =====================================================================================================
    //  defaults seed (the no-data-dir fallback; the canonical source is binds-xonotic.cfg at boot)
    // =====================================================================================================

    /// <summary>
    /// Rebuild <see cref="BindTable"/> from a <see cref="KeyBindings"/> action table (action id → key string).
    /// The canonical bind source is <c>binds-xonotic.cfg</c> ingested at boot via
    /// <see cref="RegisterBindCommands"/>; this is the fallback path <see cref="VortexArena.Game.Menu.MenuState"/>
    /// uses to seed from <see cref="KeyBindings.Defaults"/> when no data dir is mounted (so a bare/CI run is
    /// still playable). A null map is ignored (don't clobber a table the cfg already filled).
    /// </summary>
    public static void SeedFromActions(IReadOnlyDictionary<string, string>? actionBinds)
    {
        if (actionBinds is null)
            return; // canonical path already seeded the table from the cfg; don't clobber it with defaults
        BindTable.UnbindAll();
        foreach (var (id, key) in actionBinds)
            BindAction(id, key);
    }

    /// <summary>
    /// Feed one keyboard/mouse-button event to the bind table: encode the key, drop OS auto-repeat, and let
    /// <see cref="BindTable.HandleBind"/> update the held button or run the one-shot command. No-op for unbound
    /// keys, repeats, and non-key events.
    /// </summary>
    public static void HandleEvent(InputEvent ev, Action<string> runCommand)
    {
        switch (ev)
        {
            case InputEventKey k:
                if (k.Echo)
                    return;
                string? kk = KeyString(k);
                if (kk != null)
                    BindTable.HandleBind(kk, k.Pressed, runCommand);
                break;
            case InputEventMouseButton m:
                string? mk = MouseString(m.ButtonIndex);
                if (mk != null)
                    BindTable.HandleBind(mk, m.Pressed, runCommand);
                break;
        }
    }

    /// <summary>
    /// Resolve a fresh key/mouse PRESS to the console command bound to that key (DP key→bind lookup), or null for
    /// a release, OS auto-repeat, unbound key, or non-key event. Lets a global hotkey owner (the screenshot
    /// service) act on ITS bind in ANY context — the menu, an open console, a paused match — without running the
    /// whole +/- held-button machine <see cref="HandleEvent"/> drives (which would leak movement state).
    /// </summary>
    public static string? ResolvePress(InputEvent ev)
    {
        switch (ev)
        {
            case InputEventKey k when k.Pressed && !k.Echo:
                string? kk = KeyString(k);
                return string.IsNullOrEmpty(kk) ? null : NullIfEmpty(BindTable.Get(kk));
            case InputEventMouseButton m when m.Pressed:
                string? mk = MouseString(m.ButtonIndex);
                return string.IsNullOrEmpty(mk) ? null : NullIfEmpty(BindTable.Get(mk));
            default:
                return null;
        }
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    // ---- key encoding (canonical strings shared with KeyBindings; edge-independent) ----------------------

    private static string? KeyString(InputEventKey k)
    {
        Key code = k.PhysicalKeycode != Key.None
            ? DisplayServer.KeyboardGetKeycodeFromPhysical(k.PhysicalKeycode)
            : k.Keycode;
        if (code == Key.None)
            code = k.Keycode;
        if (code == Key.None)
            return null;
        string name = OS.GetKeycodeString(code);
        return name.Length == 1 ? name.ToUpperInvariant() : name;
    }

    /// <summary>
    /// Canonical mouse-button strings using ENGINE semantics (keys.c): the wheel is <c>MWHEELUP</c>/
    /// <c>MWHEELDOWN</c> and the two extra side buttons are <c>MOUSE4</c>/<c>MOUSE5</c> — matching what
    /// <c>binds-xonotic.cfg</c> binds (<c>bind MOUSE4 weaplast</c>, <c>bind MWHEELUP weapnext</c>). (The earlier
    /// convention mapped MOUSE4=wheel, which collided with the cfg; reconciled here so a cfg bind resolves.)
    /// </summary>
    private static string? MouseString(MouseButton button) => button switch
    {
        MouseButton.Left => "MOUSE1",
        MouseButton.Right => "MOUSE2",
        MouseButton.Middle => "MOUSE3",
        MouseButton.Xbutton1 => "MOUSE4",
        MouseButton.Xbutton2 => "MOUSE5",
        MouseButton.WheelUp => "MWHEELUP",
        MouseButton.WheelDown => "MWHEELDOWN",
        _ => null,
    };

    // =====================================================================================================
    //  engine key name → canonical key string (keys.c key name table → what a live event encodes to)
    // =====================================================================================================

    /// <summary>
    /// Translate a <c>binds-xonotic.cfg</c> key token (the engine names from keys.c) into the canonical string
    /// a live <see cref="InputEvent"/> encodes to (so the cfg bind matches a real press). Letters/digits and
    /// already-canonical mouse names (<c>MOUSE1</c>..<c>MOUSE5</c>, <c>MWHEELUP/DOWN</c>) pass through; named
    /// keys map to Godot's <see cref="OS.GetKeycodeString"/> spelling; joystick (<c>JOY*</c>) and other keys
    /// the client can't produce return null (skipped). Case-insensitive on the engine name.
    /// </summary>
    public static string? TranslateEngineKey(string engineName)
    {
        if (string.IsNullOrEmpty(engineName))
            return null;
        string n = engineName.Trim();

        // Already-canonical mouse names from the cfg (engine semantics) — pass straight through.
        switch (n.ToUpperInvariant())
        {
            case "MOUSE1": return "MOUSE1";
            case "MOUSE2": return "MOUSE2";
            case "MOUSE3": return "MOUSE3";
            case "MOUSE4": return "MOUSE4";
            case "MOUSE5": return "MOUSE5";
            case "MWHEELUP": return "MWHEELUP";
            case "MWHEELDOWN": return "MWHEELDOWN";
        }

        // A single printable character: a letter (canonical upper-cased like the live encoder) or a digit/symbol.
        if (n.Length == 1)
        {
            char c = n[0];
            if (char.IsLetter(c)) return char.ToUpperInvariant(c).ToString();
            return n; // digits "0".."9" and symbols ("`", "~") encode to themselves
        }

        // Named keys (keys.c) → the Godot keycode name (what OS.GetKeycodeString returns for that Key).
        if (EngineNameToGodotKey.TryGetValue(n, out Key godot))
            return OS.GetKeycodeString(godot);

        // Already a canonical Godot key name (e.g. "Up"/"Shift"/"Kp1"/"Pageup" — what we persist via
        // WriteBindings, and what a live event encodes to): pass it through so saved binds round-trip on reload.
        if (OS.FindKeycodeFromString(n) != Key.None)
            return n;

        // JOY1..JOY12 (gamepad — the client doesn't synthesise these) and anything unknown: skip.
        return null;
    }

    /// <summary>Engine key name (keys.c) → the Godot <see cref="Key"/> it corresponds to. Names whose Godot
    /// keycode string round-trips through <see cref="KeyString"/> become the canonical bind key.</summary>
    private static readonly Dictionary<string, Key> EngineNameToGodotKey = new(StringComparer.OrdinalIgnoreCase)
    {
        // modifiers / whitespace / control
        ["SHIFT"] = Key.Shift,
        ["CTRL"] = Key.Ctrl,
        ["ALT"] = Key.Alt,
        ["SPACE"] = Key.Space,
        ["ENTER"] = Key.Enter,
        ["ESCAPE"] = Key.Escape,
        ["BACKSPACE"] = Key.Backspace,
        ["TAB"] = Key.Tab,
        ["PAUSE"] = Key.Pause,
        ["CAPSLOCK"] = Key.Capslock,
        // arrows
        ["UPARROW"] = Key.Up,
        ["DOWNARROW"] = Key.Down,
        ["LEFTARROW"] = Key.Left,
        ["RIGHTARROW"] = Key.Right,
        // navigation cluster
        ["INS"] = Key.Insert,
        ["DEL"] = Key.Delete,
        ["HOME"] = Key.Home,
        ["END"] = Key.End,
        ["PGUP"] = Key.Pageup,
        ["PGDN"] = Key.Pagedown,
        // function keys
        ["F1"] = Key.F1, ["F2"] = Key.F2, ["F3"] = Key.F3, ["F4"] = Key.F4,
        ["F5"] = Key.F5, ["F6"] = Key.F6, ["F7"] = Key.F7, ["F8"] = Key.F8,
        ["F9"] = Key.F9, ["F10"] = Key.F10, ["F11"] = Key.F11, ["F12"] = Key.F12,
        // keypad (keys.c KP_*) → Godot KP_* keys
        ["KP_INS"] = Key.Kp0,
        ["KP_END"] = Key.Kp1,
        ["KP_DOWNARROW"] = Key.Kp2,
        ["KP_PGDN"] = Key.Kp3,
        ["KP_LEFTARROW"] = Key.Kp4,
        ["KP_5"] = Key.Kp5,
        ["KP_RIGHTARROW"] = Key.Kp6,
        ["KP_HOME"] = Key.Kp7,
        ["KP_UPARROW"] = Key.Kp8,
        ["KP_PGUP"] = Key.Kp9,
        ["KP_DEL"] = Key.KpPeriod,
        ["KP_SLASH"] = Key.KpDivide,
        ["KP_MULTIPLY"] = Key.KpMultiply,
        ["KP_MINUS"] = Key.KpSubtract,
        ["KP_PLUS"] = Key.KpAdd,
        ["KP_ENTER"] = Key.KpEnter,
    };

    private static string JoinTail(IReadOnlyList<string> argv, int first)
    {
        if (first >= argv.Count) return "";
        var sb = new StringBuilder();
        for (int i = first; i < argv.Count; i++)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(argv[i]);
        }
        return sb.ToString();
    }
}
