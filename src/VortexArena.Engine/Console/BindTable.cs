using System;
using System.Collections.Generic;

namespace XonoticGodot.Engine.Console;

/// <summary>
/// The runtime keybind table + the DP <c>+</c>/<c>-</c> button system — the C# successor to the engine's
/// <c>key bindings</c> map (<c>Key_SetBinding</c>) plus <c>cl_input.c</c>'s <c>kbutton_t</c> press/release
/// state. Godot-free AND dependency-light (keys are canonical strings — "W"/"Space"/"MOUSE1", case-insensitive;
/// held buttons are plain bools so this needn't reference the net <c>InputButtons</c> enum) so it is the single
/// testable source of truth the gameplay sampler reads and the console <c>bind</c>/<c>unbind</c>/<c>bindlist</c>
/// commands edit. The Godot <c>InputEvent</c> → key-string glue lives in the client (<c>Game.Console.BindInput</c>);
/// the consumer assembles the held-button bools into an <c>InputCommand</c>'s button bits.
///
/// <para>On a key event <see cref="HandleBind"/> looks up the command:</para>
/// <list type="bullet">
///   <item>a <c>+command</c> (<c>+forward</c>, <c>+attack</c>, …) sets a held button on press / clears it on
///         release — the level-triggered movement/fire state read via
///         <see cref="Forward"/>/<see cref="Side"/>/<see cref="Up"/> and the <c>*Held</c> properties.</item>
///   <item>anything else (<c>kill</c>, <c>weapon_next</c>, <c>impulse 1</c>) is a one-shot run once on the press
///         edge through the supplied command runner (the console interpreter).</item>
/// </list>
///
/// Static, matching the codebase's process-global table pattern (<c>KeyBindings</c>/<c>MenuCommand</c>/<c>Cvars</c>).
/// <see cref="Reset"/> clears all state for test isolation.
/// </summary>
public static class BindTable
{
    /// <summary>key string → command. Case-insensitive so a typed lowercase name matches the encoder's casing.</summary>
    private static readonly Dictionary<string, string> _table = new(StringComparer.OrdinalIgnoreCase);

    // held-button state (cl_input.c kbutton_t), set/cleared by +/- commands.
    private static bool _fwd, _back, _left, _right, _up, _down, _attack, _attack2, _zoom, _use, _hook;

    /// <summary>True while the scoreboard key (<c>+showscores</c>) is held — read by the HUD, not the sampler.</summary>
    public static bool ShowScores { get; private set; }

    // =============================================================================================
    //  bind table (DP bind / unbind / unbindall / bindlist)
    // =============================================================================================

    /// <summary>DP <c>bind &lt;key&gt; &lt;command&gt;</c>: set the command run while/when <paramref name="key"/> is pressed.</summary>
    public static void Bind(string key, string command)
    {
        if (!string.IsNullOrEmpty(key))
            _table[key] = command ?? "";
    }

    /// <summary>DP <c>unbind &lt;key&gt;</c>: drop a single bind. Returns true if one was removed.</summary>
    public static bool Unbind(string key) => _table.Remove(key);

    /// <summary>DP <c>unbindall</c>: clear every bind (leaves held-button state alone).</summary>
    public static void UnbindAll() => _table.Clear();

    /// <summary>DP <c>bind &lt;key&gt;</c> with no command: the current binding, or "" if unbound.</summary>
    public static string Get(string key) => _table.TryGetValue(key, out string? c) ? c : "";

    /// <summary>
    /// DP <c>getcommandkey(descriptivename, command)</c>: the display name of the first key bound to
    /// <paramref name="command"/> (e.g. "+jump" → "SPACE"); when no key is bound to it, returns the
    /// human-readable <paramref name="descriptiveName"/> fallback (QC's first arg). Matching is exact on the
    /// bound command string, like DP's bind table lookup.
    /// </summary>
    public static string CommandKey(string descriptiveName, string command)
    {
        if (!string.IsNullOrEmpty(command))
            foreach (string k in EnumerateKeysOrdered())
                if (string.Equals(_table[k], command, StringComparison.Ordinal))
                    return k;
        return descriptiveName ?? "";
    }

    private static IEnumerable<string> EnumerateKeysOrdered()
    {
        var keys = new List<string>(_table.Keys);
        keys.Sort(StringComparer.OrdinalIgnoreCase);
        return keys;
    }

    /// <summary>DP <c>bindlist</c>: every (key, command) pair, ordered by key.</summary>
    public static IEnumerable<KeyValuePair<string, string>> List()
    {
        var keys = new List<string>(_table.Keys);
        keys.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string k in keys)
            yield return new KeyValuePair<string, string>(k, _table[k]);
    }

    /// <summary>Clear the table AND all held-button state — for a clean slate (tests, profile reload).</summary>
    public static void Reset()
    {
        _table.Clear();
        ReleaseAll();
    }

    // =============================================================================================
    //  event handling (cl_input.c IN_*: key press/release → button state / one-shot command)
    // =============================================================================================

    /// <summary>
    /// Process a press/release of the canonical <paramref name="key"/> against the table. A bound
    /// <c>+command</c> sets/clears its held button; any other bound command runs once on the press edge via
    /// <paramref name="runCommand"/>. Unbound keys are ignored. (Echo/repeat filtering is the caller's job.)
    /// </summary>
    public static void HandleBind(string key, bool pressed, Action<string> runCommand)
    {
        if (key is null || !_table.TryGetValue(key, out string? command) || string.IsNullOrEmpty(command))
            return;

        if (command[0] == '+' || command[0] == '-')
        {
            // A '+'/'-' bind is normally a held engine BUTTON (movement/fire/zoom/use/hook/showscores) tracked by
            // SetButton. But a few '+'-aliases are press-TOGGLES the engine runs as commands, not held buttons —
            // notably +hud_panel_radar_maximized (the stock `m` bind → maximize the radar). SetButton has no case
            // for those, so left here they would be silently swallowed and the bind would do nothing. Route any
            // '+'/'-' command SetButton doesn't recognise to runCommand so the bound alias actually fires (the
            // command sink — e.g. NetGame.RunBoundCommand — handles the press toggle and ignores the '-' release).
            if (IsHeldButton(command))
            {
                SetButton(command, pressed);
                return;
            }
            runCommand(command); // pass both the press '+form' and the release '-form'; the sink decides
            return;
        }
        // QC togglezoom alias (binds-xonotic.cfg: bind MOUSE3 togglezoom -> ${_togglezoom}zoom -> +button4):
        // a press EDGE flips the held-zoom latch, so the stock MOUSE3 bind actually drives BUTTON_ZOOM.
        if (command == "togglezoom")
        {
            if (pressed) _zoom = !_zoom;
            return;
        }
        if (pressed)
            runCommand(command);
    }

    /// <summary>DP <c>in_releaseall</c>: clear every held button. Called when the console opens or the match
    /// pauses so a key held at that moment doesn't stay "down" once input resumes.</summary>
    public static void ReleaseAll()
    {
        _fwd = _back = _left = _right = _up = _down = false;
        _attack = _attack2 = _zoom = _use = _hook = false;
        ShowScores = false;
    }

    /// <summary>Release just the attack buttons (DP <c>-fire</c>/<c>-fire2</c> — what Base's
    /// <c>cl_unpress_attack_on_weapon_switch</c> localcmds do): the buttons stay up until the key is physically
    /// re-pressed. Used when a forced out-of-ammo weapon switch arrives with the trigger still down, so the
    /// switched-to weapon doesn't surprise-fire the moment it raises.</summary>
    public static void ReleaseAttack() => _attack = _attack2 = false;

    /// <summary>True when a '+'/'-' bind command is one of the held engine BUTTONS <see cref="SetButton"/> tracks
    /// (movement/fire/zoom/use/hook/showscores). Anything else (e.g. +hud_panel_radar_maximized) is a press-toggle
    /// alias that <see cref="HandleBind"/> dispatches to the command sink instead of latching as a button. Kept in
    /// lockstep with the <see cref="SetButton"/> switch.</summary>
    private static bool IsHeldButton(string command)
    {
        string name = command.Substring(1).ToLowerInvariant();
        return name switch
        {
            "forward" or "back" or "backward" or "moveleft" or "left" or "moveright" or "right"
                or "jump" or "moveup" or "crouch" or "movedown" or "attack" or "fire" or "attack2"
                or "altattack" or "fire2" or "zoom" or "use" or "hook" or "showscores" or "score" => true,
            _ => false,
        };
    }

    private static void SetButton(string command, bool state)
    {
        // accept the bare action name from either the +form (press) or -form (matching release).
        string name = command.Substring(1).ToLowerInvariant();
        switch (name)
        {
            case "forward": _fwd = state; break;
            case "back": case "backward": _back = state; break;
            case "moveleft": case "left": _left = state; break;
            case "moveright": case "right": _right = state; break;
            case "jump": case "moveup": _up = state; break;
            case "crouch": case "movedown": _down = state; break;
            case "attack": case "fire": _attack = state; break;
            case "attack2": case "altattack": case "fire2": _attack2 = state; break;
            case "zoom": _zoom = state; break;
            case "use": _use = state; break;
            // QC +hook (binds-xonotic.cfg: bind h +hook) -> PHYS_INPUT_BUTTON_HOOK. The +hook / offhand-fire
            // button: drives the grapple hook, the offhand blaster, and the nade prime/throw each frame.
            case "hook": _hook = state; break;
            case "showscores": case "score": ShowScores = state; break;
        }
    }

    // =============================================================================================
    //  sampled state (read by NetGame.SampleInput; consumer assembles InputButtons from the *Held bools)
    // =============================================================================================

    /// <summary>Forward axis: +forward minus +back, in [-1, 1] (the sampler rescales against sv_maxspeed).</summary>
    public static float Forward => (_fwd ? 1f : 0f) - (_back ? 1f : 0f);

    /// <summary>Side axis: +moveright minus +moveleft.</summary>
    public static float Side => (_right ? 1f : 0f) - (_left ? 1f : 0f);

    /// <summary>Up axis: +jump minus +crouch (jump/swim-up vs crouch/down).</summary>
    public static float Up => (_up ? 1f : 0f) - (_down ? 1f : 0f);

    /// <summary>True while primary fire is held (InputButtons.Attack). The sampler edge-detects this for the muzzle flash.</summary>
    public static bool AttackHeld => _attack;

    /// <summary>True while secondary fire is held (InputButtons.Attack2).</summary>
    public static bool Attack2Held => _attack2;

    /// <summary>True while jump is held (InputButtons.Jump; also raises the up axis).</summary>
    public static bool JumpHeld => _up;

    /// <summary>True while crouch is held (InputButtons.Crouch; also lowers the up axis).</summary>
    public static bool CrouchHeld => _down;

    /// <summary>True while zoom is held (InputButtons.Zoom).</summary>
    public static bool ZoomHeld => _zoom;

    /// <summary>True while use is held (InputButtons.Use).</summary>
    public static bool UseHeld => _use;

    /// <summary>True while the +hook / offhand-fire button is held (InputButtons.Hook / PHYS_INPUT_BUTTON_HOOK).
    /// Drives the offhand-weapon think (grapple hook, offhand blaster, nade prime/throw).</summary>
    public static bool HookHeld => _hook;
}
