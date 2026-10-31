using System;
using System.Collections.Generic;

namespace VortexArena.Engine.Console;

/// <summary>
/// The runtime keybind table + the DP <c>+</c>/<c>-</c> button system — the C# successor to the engine's
/// <c>key bindings</c> map (<c>Key_SetBinding</c>) plus <c>cl_input.c</c>'s <c>kbutton_t</c> press/release
/// state. Godot-free AND dependency-light (keys are canonical strings — "W"/"Space"/"MOUSE1", case-insensitive;
/// held buttons are <see cref="KeyButton"/>s so this needn't reference the net <c>InputButtons</c> enum) so it is the single
/// testable source of truth the gameplay sampler reads and the console <c>bind</c>/<c>unbind</c>/<c>bindlist</c>
/// commands edit. The Godot <c>InputEvent</c> → key-string glue lives in the client (<c>Game.Console.BindInput</c>);
/// the consumer assembles the held-button bools into an <c>InputCommand</c>'s button bits.
///
/// <para>On a key event <see cref="HandleBind"/> looks up the command:</para>
/// <list type="bullet">
///   <item>a <c>+command</c> (<c>+forward</c>, <c>+attack</c>, …) presses a held button on press and lets go of it
///         on release, per key (<see cref="KeyButton"/>: it is up only once every key holding it is) — the level-triggered movement/fire state read via
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

    // held-button state (cl_input.c kbutton_t), pressed/released by +/- commands. Each remembers WHICH keys hold
    // it (DP KeyDown/KeyUp), so releasing one of two keys bound to the same +command leaves the button down.
    private static readonly KeyButton _fwd = new(), _back = new(), _left = new(), _right = new(), _up = new(),
        _down = new(), _attack = new(), _attack2 = new(), _zoom = new(), _use = new(), _hook = new(),
        _showScores = new();

    private static readonly KeyButton[] AllButtons =
        { _fwd, _back, _left, _right, _up, _down, _attack, _attack2, _zoom, _use, _hook, _showScores };

    /// <summary>True while the scoreboard key (<c>+showscores</c>) is held — read by the HUD, not the sampler.</summary>
    public static bool ShowScores => _showScores.Down;

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
            // ButtonFor. But a few '+'-aliases are press-TOGGLES the engine runs as commands, not held buttons —
            // notably +hud_panel_radar_maximized (the stock `m` bind → maximize the radar). ButtonFor has no case
            // for those, so left here they would be silently swallowed and the bind would do nothing. Route any
            // '+'/'-' command ButtonFor doesn't recognise to runCommand so the bound alias actually fires (the
            // command sink — e.g. NetGame.RunBoundCommand — handles the press toggle and ignores the '-' release).
            if (ButtonFor(command) is { } button)
            {
                // The key rides along (DP Key_Event appends the key number to a '+' bind's press and release), so
                // the button stays down until every key that pressed it is up. A key bound to the bare '-form' is
                // not a button bind in DP (only '+' gets the key appended): its press runs `-jump` as typed, which
                // lets go whatever holds the button, and its release does nothing.
                if (command[0] == '-')
                {
                    if (pressed) button.Release(null);
                }
                else if (pressed) button.Press(key);
                else button.Release(key);
                return;
            }
            runCommand(command); // pass both the press '+form' and the release '-form'; the sink decides
            return;
        }
        // QC togglezoom alias (binds-xonotic.cfg: bind MOUSE3 togglezoom -> ${_togglezoom}zoom -> +button4):
        // a press EDGE flips the held-zoom latch, so the stock MOUSE3 bind actually drives BUTTON_ZOOM.
        // The alias runs +zoom / -zoom with no key (xonotic-client.cfg:388-389 → +button4 / -button4), which is DP's
        // "typed" form: the press holds zoom like one more key, the release clears zoom whatever else holds it.
        if (command == "togglezoom")
        {
            if (pressed)
            {
                if (_zoom.Down) _zoom.Release(null);
                else _zoom.Press(null);
            }
            return;
        }
        if (pressed)
            runCommand(command);
    }

    /// <summary>DP <c>in_releaseall</c>: clear every held button. Called when the console opens or the match
    /// pauses so a key held at that moment doesn't stay "down" once input resumes.</summary>
    public static void ReleaseAll()
    {
        foreach (KeyButton b in AllButtons)
            b.Clear();
    }

    /// <summary>
    /// DP <c>IN_*Down</c> / <c>IN_*Up</c> run as a command rather than from a key: <c>+jump</c> / <c>-jump</c> typed at
    /// the console or deferred, or with an explicit key id (<c>+jump 32</c>, the form DP's Key_Event itself sends).
    /// With no <paramref name="key"/> a press holds the button like one more key and a release clears every key
    /// holding it (cl_input.c KeyUp: "typed manually at the console, assume for unsticking, so clear all").
    /// Returns false, doing nothing, when <paramref name="command"/> is not a held engine button.
    /// </summary>
    public static bool ButtonCommand(string command, string? key)
    {
        if (string.IsNullOrEmpty(command) || (command[0] != '+' && command[0] != '-')
            || ButtonFor(command) is not { } button)
            return false;
        if (command[0] == '+') button.Press(key);
        else button.Release(key);
        return true;
    }

    /// <summary>The held buttons registered as <c>+name</c> / <c>-name</c> console commands: the names DarkPlaces
    /// (cl_input.c, sbar.c) and Xonotic's aliases (xonotic-client.cfg: fire, fire2, attack2, crouch, zoom, hook)
    /// define. <see cref="ButtonFor"/> also takes a few port-only spellings in binds (left/right as strafes,
    /// backward, altattack, score); those stay out of the console, where DP's <c>+left</c> means turn left.</summary>
    public static IReadOnlyList<string> ButtonNames { get; } = new[]
    {
        "forward", "back", "moveleft", "moveright", "jump", "moveup", "movedown", "crouch",
        "attack", "fire", "attack2", "fire2", "zoom", "use", "hook", "showscores",
    };

    /// <summary>Release just the attack buttons (DP <c>-fire</c>/<c>-fire2</c> — what Base's
    /// <c>cl_unpress_attack_on_weapon_switch</c> localcmds do): the buttons stay up until the key is physically
    /// re-pressed. Used when a forced out-of-ammo weapon switch arrives with the trigger still down, so the
    /// switched-to weapon doesn't surprise-fire the moment it raises.</summary>
    public static void ReleaseAttack()
    {
        _attack.Clear();
        _attack2.Clear();
    }

    /// <summary>
    /// Release the <c>+showscores</c> button (QC <c>HUD_Scoreboard_UI_Disable</c> sets <c>sb_showscores = false</c>,
    /// scoreboard.qc:184). Closing the interactive scoreboard while the scoreboard key is still HELD must clear the
    /// held state, or the board stays up, its fade never reaches 0, and the UI never finishes tearing down — it sits
    /// in "disabling" limbo ignoring input until the key is physically released. Re-latches on the next press.
    /// </summary>
    public static void ReleaseShowScores() => _showScores.Clear();

    /// <summary>The held engine BUTTON a '+'/'-' command drives (movement/fire/zoom/use/hook/showscores), from the bare
    /// action name of either the +form (press) or the -form (matching release); null for anything else (e.g.
    /// +hud_panel_radar_maximized), a press-toggle alias that <see cref="HandleBind"/> hands to the command sink
    /// instead of holding as a button.</summary>
    private static KeyButton? ButtonFor(string command) => command.Substring(1).ToLowerInvariant() switch
    {
        "forward" => _fwd,
        "back" or "backward" => _back,
        "moveleft" or "left" => _left,
        "moveright" or "right" => _right,
        "jump" or "moveup" => _up,
        "crouch" or "movedown" => _down,
        "attack" or "fire" => _attack,
        "attack2" or "altattack" or "fire2" => _attack2,
        "zoom" => _zoom,
        "use" => _use,
        // QC +hook (binds-xonotic.cfg: bind h +hook) -> PHYS_INPUT_BUTTON_HOOK. The +hook / offhand-fire
        // button: drives the grapple hook, the offhand blaster, and the nade prime/throw each frame.
        "hook" => _hook,
        "showscores" or "score" => _showScores,
        _ => null,
    };

    // =============================================================================================
    //  sampled state (read by NetGame.SampleInput; consumer assembles InputButtons from the *Held bools)
    // =============================================================================================

    /// <summary>Forward axis: +forward minus +back, in [-1, 1] (the sampler rescales against sv_maxspeed).</summary>
    public static float Forward => (_fwd.Down ? 1f : 0f) - (_back.Down ? 1f : 0f);

    /// <summary>Side axis: +moveright minus +moveleft.</summary>
    public static float Side => (_right.Down ? 1f : 0f) - (_left.Down ? 1f : 0f);

    /// <summary>Up axis: +jump minus +crouch (jump/swim-up vs crouch/down).</summary>
    public static float Up => (_up.Down ? 1f : 0f) - (_down.Down ? 1f : 0f);

    /// <summary>True while primary fire is held (InputButtons.Attack). The sampler edge-detects this for the muzzle flash.</summary>
    public static bool AttackHeld => _attack.Down;

    /// <summary>True while secondary fire is held (InputButtons.Attack2).</summary>
    public static bool Attack2Held => _attack2.Down;

    /// <summary>True while jump is held (InputButtons.Jump; also raises the up axis).</summary>
    public static bool JumpHeld => _up.Down;

    /// <summary>True while crouch is held (InputButtons.Crouch; also lowers the up axis).</summary>
    public static bool CrouchHeld => _down.Down;

    /// <summary>True while zoom is held (InputButtons.Zoom).</summary>
    public static bool ZoomHeld => _zoom.Down;

    /// <summary>True while use is held (InputButtons.Use).</summary>
    public static bool UseHeld => _use.Down;

    /// <summary>True while the +hook / offhand-fire button is held (InputButtons.Hook / PHYS_INPUT_BUTTON_HOOK).
    /// Drives the offhand-weapon think (grapple hook, offhand blaster, nade prime/throw).</summary>
    public static bool HookHeld => _hook.Down;
}
