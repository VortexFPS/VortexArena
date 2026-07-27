// The engine `messagemode`/`messagemode2` chat prompt — the DarkPlaces engine-side chat input line the
// original game uses for ALL chat (Base binds ENTER/T → messagemode, Y/Z → messagemode2; there is NO
// QC-side chat input). Reproduces its user-visible behavior: a "say:" / "say_team:" prompt line, typed
// text with a caret, ENTER sends (`say <text>` / `say_team <text>` through the same command channel a
// console line takes), ESCAPE cancels. While open, movement keys are released (DP key_dest = message)
// but mouse-look stays captured — and the input layer raises PHYS_INPUT_BUTTON_CHAT so the server shows
// the chat bubble (playtest #46/#48).
using Godot;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// The messagemode chat input line. Owned by the Shell (created once, lives across matches); opened by the
/// <c>messagemode</c>/<c>messagemode2</c> commands the stock binds fire (ENTER/T public, Y/Z team). The
/// submitted line goes to <see cref="Submit"/> as a full command line ("say &lt;text&gt;" / "say_team &lt;text&gt;")
/// so the host routes it exactly like a console-typed chat command.
/// </summary>
public partial class ChatPrompt : Control
{
    /// <summary>True while the prompt is open (the DP <c>key_dest == key_message</c> state). Read by the
    /// gameplay input layer: movement/fire suppressed, PHYS_INPUT_BUTTON_CHAT raised.</summary>
    public static volatile bool IsOpen;

    /// <summary>Host-wired sink for the submitted command line ("say hello" / "say_team hello").</summary>
    public System.Action<string>? Submit { get; set; }

    // DP MAX_INPUTLINE is far larger; Base chat lines are engine-truncated well below this. 255 is safe.
    private const int MaxLength = 255;

    private bool _team;
    private string _text = "";
    private float _caretPhase;

    public override void _Ready()
    {
        // Full-rect passive overlay: draws its own line, never eats mouse events (mouse-look stays live).
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        // Keep processing while the tree is paused (a solo local game auto-pauses when a UI is up) so the
        // prompt still types/closes — same reasoning as the console overlay.
        ProcessMode = ProcessModeEnum.Always;
        SetProcess(false); // only the caret blink needs _Process, and only while open (Open/Close toggle it)
    }

    /// <summary>Open the prompt (QC/DP <c>messagemode</c> = public, <c>messagemode2</c> = team chat).</summary>
    public void Open(bool team)
    {
        _team = team;
        _text = "";
        Visible = true;
        IsOpen = true;
        SetProcess(true); // run the caret-blink _Process while open
        QueueRedraw();
    }

    public void Close()
    {
        Visible = false;
        IsOpen = false;
        _text = "";
        SetProcess(false); // stop ticking when closed (the node lives for the whole session)
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        // House rule: a per-frame node ships with a Prof scope registered in FrameProfiler.TopLevelNodeScopes
        // (else it leaks into proc:other). Only runs while open (SetProcess is off otherwise), so it's free idle.
        using var _prof = XonoticGodot.Game.Client.FrameProfiler.Scope("chat");
        _caretPhase += (float)delta;
        QueueRedraw(); // caret blink (cheap: one small Control)
    }

    public override void _Input(InputEvent @event)
    {
        if (!Visible || @event is not InputEventKey key || !key.Pressed)
            return;

        // The console takes priority when open (DP opens it OVER messagemode): yield its keystrokes so it can be
        // typed. We run before it in _input (reverse tree order), so without this we'd eat everything it needs.
        if (XonoticGodot.Game.Console.ConsoleState.IsOpen)
            return;

        // Escape cancels the prompt, but the Shell owns the Escape toggle (pause menu / HUD-editor exit) and
        // closes us from there on the RELEASE edge — the only edge that reliably arrives while the mouse is
        // captured. So do NOT consume Escape: let both edges flow to Shell._UnhandledKeyInput. Consuming it here
        // would swallow the release Shell needs (leaving us open under the pause menu with a captured mouse).
        if (key.Keycode == Key.Escape)
            return;

        // The console-toggle key opens the console over the prompt (DP behavior): pass it through un-consumed to
        // ConsoleOverlay._Input instead of typing a literal backtick.
        if (key.Keycode == Key.Quoteleft)
            return;

        // The prompt owns the rest of the keyboard while open (DP key_dest = message): consume every other key so
        // binds (fire/jump) can't fire underneath; mouse events are untouched (mouse-look stays live).
        GetViewport().SetInputAsHandled();

        switch (key.Keycode)
        {
            case Key.Enter:
            case Key.KpEnter:
                if (key.Echo)
                    return; // a HELD Enter (the bind that opened us) must not auto-submit/close on its OS repeat
                string text = _text.Trim();
                if (text.Length > 0)
                    Submit?.Invoke((_team ? "say_team " : "say ") + text);
                Close();
                return;
            case Key.Backspace:
                if (_text.Length > 0)
                    _text = _text[..^1];
                QueueRedraw();
                return;
            case Key.V when key.CtrlPressed:
                string clip = DisplayServer.ClipboardGet();
                if (!string.IsNullOrEmpty(clip))
                {
                    clip = clip.Replace("\r", "").Replace("\n", " ");
                    _text += clip;
                    if (_text.Length > MaxLength) _text = _text[..MaxLength];
                }
                QueueRedraw();
                return;
        }

        // Printable characters (the engine takes any unicode the OS delivers). Guard the code point: Godot
        // normally merges surrogate pairs before delivery, but a lone surrogate / out-of-range value would make
        // ConvertFromUtf32 throw — skip those rather than crash inside _Input.
        if (key.Unicode >= 32 && key.Unicode <= 0x10FFFF
            && !(key.Unicode >= 0xD800 && key.Unicode <= 0xDFFF)
            && _text.Length < MaxLength)
        {
            _text += char.ConvertFromUtf32((int)key.Unicode);
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        if (!Visible) return;
        Font font = HudPanel.HudFont ?? ThemeDB.FallbackFont;
        int fs = Mathf.Clamp((int)(GetViewportRect().Size.Y / 40f), 14, 26);
        string prompt = _team ? "say_team:" : "say:";
        // DP draws the prompt at the top-left of the screen over the game view.
        var pos = new Vector2(8f, 8f + fs);
        string caret = (int)(_caretPhase * 2f) % 2 == 0 ? "_" : " ";
        // Faint backdrop band so the line reads over a bright sky.
        DrawRect(new Rect2(4f, 6f, GetViewportRect().Size.X * 0.6f, fs * 1.5f), new Color(0f, 0f, 0f, 0.35f));
        DrawString(font, pos, $"{prompt} {_text}{caret}", HorizontalAlignment.Left, -1f, fs,
            _team ? new Color(0.55f, 1f, 0.55f) : Colors.White);
    }
}
