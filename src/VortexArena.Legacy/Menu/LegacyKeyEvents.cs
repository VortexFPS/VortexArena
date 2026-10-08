// Port of Base/darkplaces/keys.c Key_Event and Key_ReleaseAll (with keydown[], tbl_keyascii[],
// tbl_keydest[]): where a key press goes - the console, the menu program, the client program, a bind.
using VortexArena.Legacy.Csqc;

namespace VortexArena.Legacy.Menu;

/// <summary>
/// DarkPlaces' key dispatch. A key number and the character it types go in; depending on key_dest they
/// come out as a call of the menu program (m_keydown / m_keyup), of the client program
/// (CSQC_InputEvent), or as the key's bound command put into the command buffer - "+forward 119" on the
/// press and "-forward 119" on the release for a button command, the command itself once for any other.
///
/// The rules that are easy to get wrong and that the menu depends on:
/// <list type="bullet">
/// <item>A key's release goes where its press went, even if key_dest changed in between (the click that
/// closes the menu must not arrive in the game as a button-up).</item>
/// <item>Escape is never a bind. In the game it is offered to the client program and otherwise opens the
/// menu; in the menu it is the menu's.</item>
/// <item>F1..F12 run their binds whatever has the keyboard - except while the menu has GRABBED it to ask
/// "press the key to bind", when the menu must see them.</item>
/// </list>
/// The mouse buttons and the wheel are keys like any other (K_MOUSE1 = 512 ...); the pointer's position
/// is not an event at all - the menu program polls it with getmousepos.
/// </summary>
public sealed class LegacyKeyEvents
{
    // keys.h
    public const int KEscape = 27, KShift = 134, KF1 = 135, KF12 = 146;

    private const int KeyVoid = -1;   // key_void: the event goes nowhere

    private readonly MenuKeyBindings _binds;
    private readonly Dictionary<int, (int Count, int Ascii, int Dest)> _down = new();

    public LegacyKeyEvents(MenuKeyBindings binds) => _binds = binds ?? throw new ArgumentNullException(nameof(binds));

    /// <summary>key_dest, read at the moment of each event.</summary>
    public Func<MenuKeyDest> KeyDest { get; init; } = () => MenuKeyDest.Game;
    /// <summary>key_consoleactive: the console is down and takes every key but the ones it gives back.</summary>
    public Func<bool>? ConsoleActive { get; init; }
    /// <summary>MR_KeyEvent(key, ascii, down).</summary>
    public Action<int, int, bool>? MenuKey { get; init; }
    /// <summary>MR_ToggleMenu(mode).</summary>
    public Action<int>? ToggleMenu { get; init; }
    /// <summary>CL_VM_InputEvent(type, key, ascii): true if the client program consumed the event.</summary>
    public Func<int, int, int, bool>? GameInput { get; init; }
    /// <summary>Key_Console: a key for the console's input line.</summary>
    public Action<int, int>? ConsoleKey { get; init; }
    /// <summary>Con_ToggleConsole_f.</summary>
    public Action? ToggleConsole { get; init; }
    /// <summary>Cbuf_InsertText: a bind runs ahead of what is waiting ("to avoid delays from wait commands").</summary>
    public Action<string>? InsertText { get; init; }
    /// <summary>Cbuf_AddText: the release of a button ("to ensure it's after the +bind").</summary>
    public Action<string>? AddText { get; init; }

    /// <summary>keydown[key]: 0 up, 1 down, 2 repeating.</summary>
    public int Down(int key) => _down.TryGetValue(key, out (int Count, int Ascii, int Dest) state) ? state.Count : 0;

    /// <summary>Key_Event.</summary>
    public void Event(int key, int ascii, bool down)
    {
        if (key < 0 || key >= CsqcKeys.MaxKeys) return;

        string? bind = _binds.GetBind(key, -1);
        // "key_consoleactive is a flag not a key_dest because the console is a high priority overlay"
        int dest = ConsoleActive?.Invoke() == true ? KeyConsole : (int)KeyDest();

        _down.TryGetValue(key, out (int Count, int Ascii, int Dest) state);
        if (down)
        {
            // "increment key repeat count each time a down is received so that things which want to
            // ignore key repeat can ignore it"
            state.Count = Math.Min(state.Count + 1, 2);
            if (state.Count == 1)
            {
                state.Ascii = ascii;
                state.Dest = dest;
            }
            else
            {
                ascii = state.Ascii;
                dest = state.Dest;
            }
            _down[key] = state;
        }
        else
        {
            // "clear repeat count now that the key is released": it goes where the press went. A key
            // that was never seen going down (pressed before the window had focus) goes to today's dest.
            if (state.Count != 0)
            {
                dest = state.Dest;
                ascii = state.Ascii;
            }
            state.Count = 0;
            _down.Remove(key);
        }
        int count = down ? state.Count : 0;

        if (dest == KeyVoid) return;

        // "specially handle escape (togglemenu) and shift-escape (toggleconsole) engine bindings, these
        // are not handled as normal binds so that the user can recover from a completely empty bindmap"
        if (key == KEscape)
        {
            if (count > 1) return;   // "ignore key repeats on escape"
            if (Down(KShift) != 0)
            {
                if (down)
                {
                    ToggleConsole?.Invoke();
                    SetDest(key, KeyVoid);   // "esc release should go nowhere"
                }
                return;
            }
            switch (dest)
            {
                case KeyConsole:
                    if (down) ToggleConsole?.Invoke();
                    break;
                case (int)MenuKeyDest.Message:
                    break;   // the chat line is the owner's (it closes itself on Escape)
                case (int)MenuKeyDest.Menu:
                case (int)MenuKeyDest.MenuGrabbed:
                    MenuKey?.Invoke(key, ascii, down);
                    break;
                case (int)MenuKeyDest.Game:
                    // "csqc has priority over toggle menu if it wants to"
                    bool consumed = GameInput?.Invoke(down ? 0 : 1, key, ascii) == true;
                    if (!consumed && down) ToggleMenu?.Invoke(1);
                    break;
            }
            return;
        }

        // "send function keydowns to interpreter no matter what mode is (unless the menu has
        // specifically grabbed the keyboard, for rebinding keys)"
        if (dest != (int)MenuKeyDest.MenuGrabbed && key >= KF1 && key <= KF12)
        {
            if (bind is not null) RunBind(bind, key, down, count);
            return;
        }

        if (dest == KeyConsole)
        {
            if (down) ConsoleKey?.Invoke(key, ascii);
            return;
        }

        // "handle toggleconsole in menu too". (con_closeontoggleconsole and the colour-prefix exemption
        // for German keyboards are the console's concern; the bind itself is honoured.)
        if (dest == (int)MenuKeyDest.Menu && down && bind is not null && bind.StartsWith("toggleconsole", StringComparison.Ordinal) && ascii != '^')
        {
            InsertText?.Invoke("toggleconsole\n");
            SetDest(key, KeyVoid);
            return;
        }

        switch (dest)
        {
            case (int)MenuKeyDest.Message:
                break;
            case (int)MenuKeyDest.Menu:
            case (int)MenuKeyDest.MenuGrabbed:
                MenuKey?.Invoke(key, ascii, down);
                break;
            case (int)MenuKeyDest.Game:
                // "ignore key repeats on binds and only send the bind if the event hasnt been already
                // processed by csqc"
                bool consumed = GameInput?.Invoke(down ? 0 : 1, key, ascii) == true;
                if (!consumed && bind is not null) RunBind(bind, key, down, count);
                break;
        }
    }

    private const int KeyConsole = 100;   // key_console, which no program is ever told about

    private void SetDest(int key, int dest)
    {
        if (_down.TryGetValue(key, out (int Count, int Ascii, int Dest) state)) _down[key] = (state.Count, state.Ascii, dest);
    }

    // "button commands add keynum as a parm"
    private void RunBind(string bind, int key, bool down, int count)
    {
        if (count == 1 && down)
        {
            if (bind[0] == '+') InsertText?.Invoke($"{bind} {key}\n");
            else InsertText?.Invoke(bind + "\n");
        }
        else if (bind[0] == '+' && !down && count == 0) AddText?.Invoke($"-{bind[1..]} {key}\n");
    }

    /// <summary>
    /// Key_ReleaseAll: a release for every key that is down, as when the window loses focus or the menu
    /// is restarted - "now all keys are guaranteed up and only future events count".
    /// </summary>
    public void ReleaseAll()
    {
        foreach (int key in new List<int>(_down.Keys)) Event(key, 0, false);
        _down.Clear();
    }
}
