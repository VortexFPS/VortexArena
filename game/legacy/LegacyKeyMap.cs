// Port of Base/darkplaces/keys.h keynum_t as seen from the window system: the table vid_sdl.c MapKey
// implements for SDL scancodes, here for Godot's key codes. Key_Event (keys.c) takes a key number and
// the character it types; this produces both.
using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Legacy.Csqc;

namespace VortexArena.Game.Legacy;

/// <summary>
/// Godot input events to DarkPlaces key numbers, which is what a client program is handed in
/// CSQC_InputEvent and compares against the K_* constants compiled into it.
/// </summary>
public static class LegacyKeyMap
{
    // Named keys: Godot's key to DarkPlaces' name for it (keys.c keynames[]); the number comes from CsqcKeys.
    private static readonly Dictionary<Key, string> Named = new()
    {
        [Key.Tab] = "TAB", [Key.Enter] = "ENTER", [Key.Escape] = "ESCAPE", [Key.Space] = "SPACE", [Key.Backspace] = "BACKSPACE",
        [Key.Up] = "UPARROW", [Key.Down] = "DOWNARROW", [Key.Left] = "LEFTARROW", [Key.Right] = "RIGHTARROW",
        [Key.Alt] = "ALT", [Key.Ctrl] = "CTRL", [Key.Shift] = "SHIFT",
        [Key.F1] = "F1", [Key.F2] = "F2", [Key.F3] = "F3", [Key.F4] = "F4", [Key.F5] = "F5", [Key.F6] = "F6",
        [Key.F7] = "F7", [Key.F8] = "F8", [Key.F9] = "F9", [Key.F10] = "F10", [Key.F11] = "F11", [Key.F12] = "F12",
        [Key.Insert] = "INS", [Key.Delete] = "DEL", [Key.Pagedown] = "PGDN", [Key.Pageup] = "PGUP", [Key.Home] = "HOME", [Key.End] = "END",
        [Key.Pause] = "PAUSE", [Key.Numlock] = "NUMLOCK", [Key.Capslock] = "CAPSLOCK", [Key.Scrolllock] = "SCROLLOCK",
        [Key.Kp0] = "KP_INS", [Key.Kp1] = "KP_END", [Key.Kp2] = "KP_DOWNARROW", [Key.Kp3] = "KP_PGDN", [Key.Kp4] = "KP_LEFTARROW",
        [Key.Kp5] = "KP_5", [Key.Kp6] = "KP_RIGHTARROW", [Key.Kp7] = "KP_HOME", [Key.Kp8] = "KP_UPARROW", [Key.Kp9] = "KP_PGUP",
        [Key.KpPeriod] = "KP_DEL", [Key.KpDivide] = "KP_SLASH", [Key.KpMultiply] = "KP_MULTIPLY", [Key.KpSubtract] = "KP_MINUS",
        [Key.KpAdd] = "KP_PLUS", [Key.KpEnter] = "KP_ENTER", [Key.Print] = "PRINTSCREEN",
    };

    private static readonly Dictionary<Key, int> NamedNumbers = BuildNamedNumbers();
    private static readonly Dictionary<int, Key> NumberToKey = BuildNumberToKey();

    private static Dictionary<Key, int> BuildNamedNumbers()
    {
        Dictionary<Key, int> map = new();
        foreach ((Key key, string name) in Named)
        {
            int number = CsqcKeys.StringToKeynum(name);
            if (number > 0) map[key] = number;
        }
        return map;
    }

    private static Dictionary<int, Key> BuildNumberToKey()
    {
        Dictionary<int, Key> map = new();
        foreach ((Key key, int number) in NamedNumbers) map.TryAdd(number, key);
        return map;
    }

    /// <summary>
    /// The DarkPlaces key number of a key event, or -1 for a key DarkPlaces has no number for. Letters
    /// are their lower-case ASCII code and other printable keys their unshifted character, as in keys.h.
    /// </summary>
    public static int KeyNumber(InputEventKey key)
    {
        Key code = key.PhysicalKeycode != Key.None ? DisplayServer.KeyboardGetKeycodeFromPhysical(key.PhysicalKeycode) : key.Keycode;
        if (code == Key.None) code = key.Keycode;
        if (NamedNumbers.TryGetValue(code, out int named)) return named;
        long value = (long)code;
        // Godot's codes for printable keys are their upper-case Latin-1 characters.
        if (value is >= 'A' and <= 'Z') return (int)value + 32;
        if (value is > 32 and < 127) return (int)value;
        return -1;
    }

    /// <summary>The character the event types (Key_Event's "ascii" argument), 0 for none or on release.</summary>
    public static int Character(InputEventKey key)
    {
        if (!key.Pressed) return 0;
        long unicode = key.Unicode;
        return unicode is >= 32 and < 0xFFFF and not 127 ? (int)unicode : 0;
    }

    /// <summary>MOUSE1..MOUSE5 and the wheel (K_MOUSE1 = 512 ...), or -1.</summary>
    public static int MouseButtonNumber(MouseButton button) => button switch
    {
        MouseButton.Left => 512,
        MouseButton.Right => 513,
        MouseButton.Middle => 514,
        MouseButton.WheelUp => 515,
        MouseButton.WheelDown => 516,
        MouseButton.Xbutton1 => 517,
        MouseButton.Xbutton2 => 518,
        _ => -1,
    };

    /// <summary>
    /// A DarkPlaces key number as the string the player's bind table is keyed by (the spelling
    /// <c>BindInput</c> gives a live event), or null for a key the table cannot hold. This is the
    /// read-only bridge behind the program's getkeybind and findkeysforcommand.
    /// </summary>
    public static string? BindTableKey(int keyNumber)
    {
        switch (keyNumber)
        {
            case 512: return "MOUSE1";
            case 513: return "MOUSE2";
            case 514: return "MOUSE3";
            case 515: return "MWHEELUP";
            case 516: return "MWHEELDOWN";
            case 517: return "MOUSE4";
            case 518: return "MOUSE5";
        }
        if (NumberToKey.TryGetValue(keyNumber, out Key named)) return OS.GetKeycodeString(named);
        if (keyNumber is >= 'a' and <= 'z') return ((char)(keyNumber - 32)).ToString();
        if (keyNumber is > 32 and < 127) return ((char)keyNumber).ToString();
        return null;
    }

    /// <summary>The DarkPlaces key numbers the player's bind table can hold: what findkeysforcommand walks.</summary>
    public static IEnumerable<int> BindableKeyNumbers()
    {
        for (int c = 33; c < 127; c++)
            if (c is not (>= 'A' and <= 'Z')) yield return c;
        foreach (int number in NumberToKey.Keys) yield return number;
        for (int mouse = 512; mouse <= 518; mouse++) yield return mouse;
    }
}
