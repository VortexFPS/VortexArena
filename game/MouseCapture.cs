using Godot;

namespace XonoticGodot.Game;

/// <summary>
/// The single owner of the OS mouse-grab state. FPS mouse-look wants the pointer <see cref="Input.MouseModeEnum.Captured"/>
/// (locked to the window); menus, the console, the pause screen, and minigame UI want it free
/// (<see cref="Input.MouseModeEnum.Visible"/>). Every subsystem declares its intent through
/// <see cref="SetWantCapture"/> instead of poking <see cref="Input.MouseMode"/> directly, so there is one place
/// that decides the real mode.
///
/// <para><b>Why this exists.</b> Capturing the cursor while the window is <em>unfocused</em> sets a system-wide
/// pointer confine (Win32 <c>ClipCursor</c>) that traps the mouse in the game's screen rectangle even while the
/// user is working in another app — and it isn't released until focus next changes. So a match started (or left
/// running) while Xonotic is in the background — a scripted/background <c>--host</c> launch, or a match that ends
/// while alt-tabbed away — would steal the cursor. This coordinator ANDs the game's desired capture with the
/// window's actual focus, so we only ever grab the pointer while Xonotic is the focused window and release it the
/// instant focus is lost. The focus edges are fed in by <see cref="Shell._Notification"/>.</para>
/// </summary>
public static class MouseCapture
{
    /// <summary>What the current game state wants: true = captured for gameplay, false = free for UI.</summary>
    private static bool _wantCapture;

    /// <summary>Window focus, tracked from the focus notifications. Defaults true; the live DisplayServer query
    /// in <see cref="Apply"/> covers the boot case where no focus edge has arrived yet (an app that starts
    /// unfocused).</summary>
    private static bool _focused = true;

    /// <summary>Declare whether the current game state wants the pointer captured (gameplay) or free (menu /
    /// console / pause / minigame UI). The pointer is actually grabbed only when this is true <em>and</em> the
    /// window is focused.</summary>
    public static void SetWantCapture(bool want)
    {
        _wantCapture = want;
        Apply();
    }

    private static bool _hudEditorWantsCursor;

    /// <summary>
    /// HUD configure-mode override (the <c>hud_config.qc</c> port): while <c>_hud_configure 1</c> the editor
    /// needs the OS pointer FREE so panels can be hovered/clicked/dragged — Base shows the engine cursor over
    /// the live game there (<c>cursor_type</c>). The net layer's per-frame reassert
    /// (<c>SetWantCapture(!UiOwnsCursor)</c>) doesn't know about the editor, so instead of fighting it frame-
    /// by-frame the editor raises this override and <see cref="Apply"/> resolves Visible while it's up.
    /// Set/cleared by <c>HudConfigEditor.Update</c> on the configure-mode edges.
    /// </summary>
    public static bool HudEditorWantsCursor
    {
        get => _hudEditorWantsCursor;
        set
        {
            if (_hudEditorWantsCursor == value) return;
            _hudEditorWantsCursor = value;
            Apply();
        }
    }

    private static bool _menuWantsCursor;

    /// <summary>
    /// Shell menu override: while the menu layer (main menu or the in-game pause menu) is visible the OS pointer
    /// must be FREE. The pause-menu path used to rely on the #19 auto-pause (<c>GetTree().Paused</c>) to stop
    /// NetGame's per-frame capture reassert (<c>SetWantCapture(!UiOwnsCursor)</c>) — but the tree does NOT pause
    /// with <c>cl_autopause 0</c> or with remote clients connected, so the reassert re-grabbed the pointer the
    /// frame after Esc freed it and the pause menu sat under an invisible cursor. Set on the menu edges
    /// (<c>Shell.OpenPauseMenu</c>/<c>Resume</c>) and re-synced per frame from <c>Shell._Process</c> off
    /// <c>_menu.Visible</c> (same reassert-not-edge-latch reasoning as NetGame's cursor block).
    /// </summary>
    public static bool MenuWantsCursor
    {
        get => _menuWantsCursor;
        set
        {
            if (_menuWantsCursor == value) return;
            _menuWantsCursor = value;
            Apply();
        }
    }

    /// <summary>Feed a window-focus edge (from <see cref="Shell._Notification"/>). Re-applies the mode so the
    /// grab is dropped the moment focus leaves and restored when it returns.</summary>
    public static void SetFocused(bool focused)
    {
        _focused = focused;
        Apply();
    }

    /// <summary>True when the game window is REALLY the focused/foreground window, per BOTH Godot's focus flag
    /// AND (on Windows) the OS itself. The Win32 cross-check matters for a window LAUNCHED into the background
    /// (a scripted <c>--host</c> while the user works in another app): Godot's internal flag can sit stale-TRUE
    /// (initialized focused; no <c>WM_KILLFOCUS</c> ever arrives because real focus was never gained), which let
    /// the spawn-frame capture set the system-wide <c>ClipCursor</c> confine and trap the pointer inside the
    /// game's window border while ANOTHER app was foreground (playtest #28). <see cref="Shell"/> polls this per
    /// frame so a stale confine also releases without needing a focus edge.</summary>
    public static bool WindowReallyFocused() => DisplayServer.WindowIsFocused() && OsForeground();

    private static bool OsForeground()
    {
        if (!System.OperatingSystem.IsWindows())
            return true;
        long h = DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle);
        return h == 0 || GetForegroundWindow() == (nint)h; // 0 = no native handle (headless) → no cross-check
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private static void Apply()
    {
        // Release aggressively: the focus-out edge, the live Godot query, OR the OS foreground cross-check saying
        // "not focused" is enough to free the pointer. The live checks also cover a background launch, where
        // want-capture can be requested before any focus edge has been delivered.
        bool focused = _focused && WindowReallyFocused();
        Input.MouseModeEnum target = _wantCapture && !_hudEditorWantsCursor && !_menuWantsCursor && focused
            ? Input.MouseModeEnum.Captured
            : Input.MouseModeEnum.Visible;
        if (Input.MouseMode != target)
            Input.MouseMode = target;
    }
}
