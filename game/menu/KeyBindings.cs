using System;
using System.Collections.Generic;
using Godot;

namespace VortexArena.Game.Menu;

/// <summary>
/// The keybind catalog + the encode/decode glue between the menu's string-based bind table and Godot's
/// typed <see cref="Key"/>/<see cref="MouseButton"/>/<see cref="InputEvent"/>. This is the C# successor
/// to QC's keybind machinery (qcsrc/menu/xonotic/keybinder.qc): an ordered list of bindable actions and the
/// helpers that turn a captured input event into a stored string and back.
///
/// <para>The runtime bind table itself is now loaded from the canonical <c>binds-xonotic.cfg</c> at boot
/// (<see cref="VortexArena.Game.Console.BindInput.RegisterBindCommands"/> → <c>VortexArena.Engine.Console.BindTable</c>);
/// this class is the <em>display/capture</em> layer the input-settings dialog uses (the action list + the
/// key encode/decode), not a parallel store. <see cref="Defaults"/> survives only as a thin fallback for when
/// no cfg is mounted.</para>
///
/// Bind string format (engine-bind-ish, human readable in the config), matching what
/// <see cref="VortexArena.Game.Console.BindInput"/> encodes a live event to:
///   * printable / named keyboard keys  -> Godot's <see cref="OS.GetKeycodeString"/> name, e.g.
///     "W", "Space", "Ctrl", "F1", "Up".  (Single ASCII letters are upper-cased for a tidy label.)
///   * mouse buttons                    -> "MOUSE1".."MOUSE5" for left/right/middle + the two X-buttons, and
///     "MWHEELUP"/"MWHEELDOWN" for the wheel — DP engine semantics (keys.c), matching binds-xonotic.cfg.
/// </summary>
public static class KeyBindings
{
    /// <summary>
    /// The bindable actions in display order, with a stable id (used as the config key) and a human label for
    /// the row. The id maps to a DP command in <see cref="VortexArena.Game.Console.BindInput"/>'s ActionToCommand;
    /// the vocabulary mirrors the core rows keybinder.qc's KeyBinds_BuildList lists (Moving / Attacking /
    /// Weapons / View / Communication / Misc).
    /// </summary>
    public static readonly (string Id, string Label)[] Actions =
    {
        // Moving
        ("forward",   "Move forward"),
        ("back",      "Move backward"),
        ("left",      "Strafe left"),
        ("right",     "Strafe right"),
        ("jump",      "Jump / swim"),
        ("crouch",    "Crouch / sink"),
        ("hook",      "Off-hand hook"),
        // Attacking
        ("attack",    "Primary fire"),
        ("attack2",   "Secondary fire"),
        // Weapons
        ("weapprev",  "Previous weapon"),
        ("weapnext",  "Next weapon"),
        ("weaplast",  "Previously used weapon"),
        ("weapbest",  "Best weapon"),
        ("reload",    "Reload"),
        ("dropweapon","Drop weapon"),
        // View
        ("zoom",      "Hold zoom"),
        ("togglezoom","Toggle zoom"),
        ("scoreboard","Show scores"),
        ("screenshot","Screenshot"),
        ("botpolicies","Bot movement lab"),
        ("here_waypoint","Place HERE waypoint"),
        // Communication
        ("chat",      "Public chat"),
        ("chat_team", "Team chat"),
        // Misc
        ("use",       "Use / Interact"),
        ("kill",      "Suicide / respawn"),
        ("console",   "Enter console"),
    };

    /// <summary>
    /// Fallback default key for each action id, used <em>only</em> when no <c>binds-xonotic.cfg</c> is mounted
    /// (e.g. a bare test/CI run). The canonical defaults are the cfg itself, ingested at boot; these are kept as
    /// canonical key strings (matching <see cref="VortexArena.Game.Console.BindInput"/>'s encoder) so the menu still
    /// shows a sensible face without a data dir. Reduced from the previous invented table (T15) — the cfg is the
    /// real source of truth.
    /// </summary>
    public static readonly Dictionary<string, string> Defaults = new()
    {
        ["forward"]    = "W",
        ["back"]       = "S",
        ["left"]       = "A",
        ["right"]      = "D",
        ["jump"]       = "Space",
        ["crouch"]     = "Shift",
        ["attack"]     = "MOUSE1",
        ["attack2"]    = "MOUSE2",
        ["weapnext"]   = "MWHEELUP",
        ["weapprev"]   = "MWHEELDOWN",
        ["weaplast"]   = "Q",
        ["reload"]     = "R",
        ["zoom"]       = "MOUSE3",
        ["use"]        = "F",
        ["scoreboard"] = "Tab",
        ["screenshot"] = "F12",
        ["botpolicies"] = "F10",
        ["here_waypoint"] = "P",
    };

    /// <summary>The <see cref="InputMap"/> action name for a bind id (namespaced to avoid clashes).</summary>
    public static string InputActionName(string id) => "rebirth_" + id;

    // -------------------------------------------------------------------------------------------------
    //  Capture: turn a raw input event into a stored bind string (null = not a bindable event).
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Encode a captured input event to a bind string, or return null if the event isn't a key/mouse
    /// press we want to bind (e.g. a key release, an echo/repeat, or mouse motion).
    /// </summary>
    public static string? Encode(InputEvent ev)
    {
        switch (ev)
        {
            case InputEventKey k when k.Pressed && !k.Echo:
            {
                // Prefer the physical keycode so binds are layout-stable; fall back to the logical one.
                Key code = k.PhysicalKeycode != Key.None
                    ? DisplayServer.KeyboardGetKeycodeFromPhysical(k.PhysicalKeycode)
                    : k.Keycode;
                if (code == Key.None)
                    code = k.Keycode;
                return EncodeKey(code);
            }
            case InputEventMouseButton m when m.Pressed:
                return EncodeMouse(m.ButtonIndex);
            default:
                return null;
        }
    }

    private static string EncodeKey(Key code)
    {
        if (code == Key.None)
            return "";
        string name = OS.GetKeycodeString(code);
        // Single ASCII letters look tidiest upper-cased on the button face ("W", not "w").
        return name.Length == 1 ? name.ToUpperInvariant() : name;
    }

    /// <summary>Engine mouse-button semantics (keys.c), matching <see cref="VortexArena.Game.Console.BindInput"/>:
    /// the two X-buttons are MOUSE4/MOUSE5 and the wheel is MWHEELUP/MWHEELDOWN.</summary>
    private static string EncodeMouse(MouseButton button) => button switch
    {
        MouseButton.Left => "MOUSE1",
        MouseButton.Right => "MOUSE2",
        MouseButton.Middle => "MOUSE3",
        MouseButton.Xbutton1 => "MOUSE4",
        MouseButton.Xbutton2 => "MOUSE5",
        MouseButton.WheelUp => "MWHEELUP",
        MouseButton.WheelDown => "MWHEELDOWN",
        _ => "MOUSE" + (int)button,
    };

    // -------------------------------------------------------------------------------------------------
    //  Playback: turn a stored bind string back into an InputEvent for the InputMap / a row face.
    // -------------------------------------------------------------------------------------------------

    /// <summary>Decode a bind string into an <see cref="InputEvent"/>, or null if it's empty/unknown.</summary>
    public static InputEvent? ToInputEvent(string bind)
    {
        if (string.IsNullOrEmpty(bind))
            return null;

        string upper = bind.ToUpperInvariant();
        if (upper is "MWHEELUP" or "MWHEELDOWN" || bind.StartsWith("MOUSE", StringComparison.OrdinalIgnoreCase))
        {
            MouseButton button = upper switch
            {
                "MOUSE1" => MouseButton.Left,
                "MOUSE2" => MouseButton.Right,
                "MOUSE3" => MouseButton.Middle,
                "MOUSE4" => MouseButton.Xbutton1,
                "MOUSE5" => MouseButton.Xbutton2,
                "MWHEELUP" => MouseButton.WheelUp,
                "MWHEELDOWN" => MouseButton.WheelDown,
                _ => MouseButton.None,
            };
            if (button == MouseButton.None)
                return null;
            return new InputEventMouseButton { ButtonIndex = button, Pressed = true };
        }

        Key code = OS.FindKeycodeFromString(bind);
        if (code == Key.None)
            return null;
        return new InputEventKey { Keycode = code, PhysicalKeycode = code, Pressed = true };
    }
}
