using Godot;
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;

namespace VortexArena.Game.Hud;

/// <summary>
/// Frames-per-second readout — the C# successor to Darkplaces' <c>Sbar_ShowFPS</c> / <c>Sbar_ShowFPS_Update</c>
/// (Base/darkplaces/sbar.c), gated on the <c>cl_showfps</c> cvar (virtual alias <c>showfps</c>, the name the
/// video-settings checkbox binds — DialogSettingsVideo.cs). It reproduces DP's exact behaviour:
///
/// <list type="bullet">
///   <item><b>Measurement</b> — a frame counter accumulated over a 1-second window, the rate recomputed once per
///         second as <c>framecount / elapsed</c> (DP <c>Sbar_ShowFPS_Update</c>), so the number holds steady for
///         a second rather than flickering every frame. The same &gt;1.5×-interval stall resync is kept.</item>
///   <item><b>Format</b> — <c>cl_showfps 1</c> draws <c>"%4i fps"</c>; below 1 fps it flips to <c>"%4i spf"</c>
///         (seconds-per-frame) and turns red, exactly like DP's <c>red</c> branch; <c>cl_showfps 2</c> draws
///         <c>"%7.3f mspf"</c> (milliseconds-per-frame).</item>
///   <item><b>Placement</b> — bottom-right corner, right-aligned, over a translucent black backing fill spanning
///         to the screen edge (DP <c>DrawQ_Fill</c> + <c>DrawQ_String</c> at <c>FONT_INFOBAR</c>).</item>
/// </list>
///
/// On top of the faithful cvar gate, the panel is <b>on by default in debug builds</b> (running from the Godot
/// editor or a debug export, <see cref="OS.IsDebugBuild"/>) unless the player has explicitly changed the cvar
/// from its default — so a developer always sees the framerate without touching a setting, while a shipped
/// release stays clean unless <c>showfps</c> is turned on.
///
/// Unlike the other panels this one is screen-global (it owns no player state); <see cref="Hud"/> gives it the
/// full viewport rect so it can right-align against the screen edge, and it drives its own per-frame counter +
/// redraw in <see cref="_Process"/>.
/// </summary>
public partial class FpsPanel : HudPanel
{
    // DP keeps the rate fresh once per second; the held value is what avoids per-frame flicker.
    private const double Interval = 1.0;

    private double _clock;          // monotonic accumulated real time (DP host.realtime)
    private double _nextTime = Interval;
    private double _lastTime;       // start of the current measurement window
    private int _frameCount;
    private double _frameRate;      // frames per second over the last completed window
    private bool _haveWindow;       // false until the first full second elapses (seed provisionally until then)

    /// <summary>Always animating — the counter advances and the held value can change each second.</summary>
    public override bool IsDynamic => true;

    public override void _Process(double delta)
    {
        // --- DP Sbar_ShowFPS_Update: count frames, recompute the rate at each 1-second boundary ---
        _clock += delta;
        _frameCount++;
        if (_clock >= _nextTime)
        {
            _frameRate = _frameCount / (_clock - _lastTime);
            // Resync after a long stall (a hitch longer than 1.5 intervals) so the window doesn't sprint to catch up.
            if (_nextTime < _clock - Interval * 1.5)
                _nextTime = _clock;
            _lastTime = _clock;
            _nextTime += Interval;
            _frameCount = 0;
            _haveWindow = true;
        }
        // Before the first window closes, show a live provisional rate instead of 0 (DP shows garbage for ~1s; we don't).
        if (!_haveWindow)
            _frameRate = _frameCount / System.Math.Max(_clock - _lastTime, 1e-6);

        // Self-driven visibility + redraw (this panel owns no player, so it doesn't rely on Hud.SetPlayer).
        // Redraw only when the displayed text actually changes (~1×/s, when _frameRate updates) — not every
        // frame (3.2-3): re-recording the canvas + re-formatting the string 160×/s for a number that ticks
        // once a second is the wasteful pattern the report flags.
        bool show = ShowMode() != 0;
        if (show != Visible)
            Visible = show;
        if (show && NeedsRedraw())
            QueueRedraw();
    }

    // Last-drawn snapshot for the change gate (alloc-free primitive compare, not a formatted string).
    private int _lastMode = -1;
    private int _lastShown = int.MinValue;
    private int _lastWidth, _lastHeight;

    /// <summary>True only when the drawn text or layout changed: the held rate (updated ~1×/s) crossing the
    /// displayed-value boundary, a mode switch, or a viewport resize (3.2-3).</summary>
    public override bool NeedsRedraw()
    {
        int mode = ShowMode();
        // The integer the panel prints: fps (or spf when <1) for mode 1, mspf×1000 for mode 2 — both derived
        // purely from _frameRate, which only changes at each 1-second window boundary.
        double safe = System.Math.Max(_frameRate, 1e-6);
        int shown = mode >= 2
            ? (int)(1000.0 / safe * 1000.0)            // mspf to ~3-decimal resolution
            : _frameRate < 1.0 ? -(int)(1.0 / safe + 0.5) : (int)(_frameRate + 0.5);
        int w = (int)Size2.X, h = (int)Size2.Y;
        if (mode == _lastMode && shown == _lastShown && w == _lastWidth && h == _lastHeight)
            return false;
        _lastMode = mode; _lastShown = shown; _lastWidth = w; _lastHeight = h;
        return true;
    }

    /// <summary>
    /// The effective <c>cl_showfps</c> mode: 0 = off, 1 = fps (spf when &lt;1), 2 = mspf. Reads <c>showfps</c>
    /// (the menu-bound name) then DP's <c>cl_showfps</c>; in a debug build it defaults to 1 unless the player has
    /// changed the cvar from its default (so devs always see it, releases stay opt-in).
    /// </summary>
    private static int ShowMode()
    {
        if (Api.Services is null)
            return OS.IsDebugBuild() ? 1 : 0;

        ICvarService cv = Api.Cvars;
        int mode = (int)cv.GetFloat("showfps");
        if (mode == 0)
            mode = (int)cv.GetFloat("cl_showfps");
        if (mode != 0)
            return mode;

        // Debug default-on, but never override an explicit player choice. IsModified is false when the cvar is
        // absent OR still at its default, so a fresh/default store enables it in debug while a player who turned
        // it on (mode != 0 above) or to a non-default value gets their setting honoured.
        if (OS.IsDebugBuild() && cv is CvarService cs && !cs.IsModified("showfps") && !cs.IsModified("cl_showfps"))
            return 1;
        return 0;
    }

    protected override void DrawPanel()
    {
        int mode = ShowMode();
        if (mode == 0)
            return;

        bool red = _frameRate < 1.0;          // DP: under 1 fps, report seconds-per-frame in red
        double safe = System.Math.Max(_frameRate, 1e-6);
        string text = mode >= 2
            ? $"{1000.0 / safe,7:0.000} mspf"
            : red
                ? $"{(int)(1.0 / safe + 0.5),4} spf"
                : $"{(int)(_frameRate + 0.5),4} fps";

        // Size scales gently with the viewport so it reads like DP's FONT_INFOBAR at any resolution.
        int size = (int)Mathf.Clamp(Size2.Y * 0.018f, 12f, 28f);
        Color color = red ? new Color(1f, 0f, 0f, 1f) : new Color(1f, 1f, 1f, 1f);

        float tw = MeasureText(text, size);
        float lineH = size + 4f;
        float x = Size2.X - tw - 4f;          // right-aligned, small inset from the screen edge
        float y = Size2.Y - lineH;            // bottom row (DP vid_conheight - sbar_info_pos - lines)

        // Translucent black backing from the text to the right edge (DP DrawQ_Fill at 0.5 alpha).
        DrawRect(new Rect2(x - 2f, y, Size2.X - (x - 2f), lineH), new Color(0f, 0f, 0f, 0.5f));
        DrawText(new Vector2(x, y), text, color, size);
    }
}
