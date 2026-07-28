using Godot;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Simulation;
using NVec3 = System.Numerics.Vector3;

namespace XonoticGodot.Game.Hud;

/// <summary>
/// World-coordinate readout — the position sibling of <see cref="FpsPanel"/>/<see cref="PingPanel"/>, drawn in
/// the bottom-right corner stacked above the FPS/ping lines. It surfaces the local player's <b>Quake-space</b>
/// map coordinates (the same X Y Z space map entities and Darkplaces' <c>cl_showpos</c> use — NOT the scaled,
/// Y-up Godot transform), gated on a dedicated <c>cl_showposition</c> cvar (virtual alias <c>showposition</c>,
/// the name the video-settings checkbox binds — DialogSettingsVideo.cs). Like the FPS overlay it is a deliberate
/// port extra rather than a 1:1 DP port.
///
/// <list type="bullet">
///   <item><b>Source</b> — the value comes from <see cref="PositionProvider"/>, which the net layer wires to
///         <see cref="XonoticGodot.Game.Net.ClientNet.PredictedOrigin"/> (the local player's predicted physics
///         origin). It returns <c>null</c> when there's no live local body (menu / pre-spawn / model viewer), and
///         the panel then draws nothing.</item>
///   <item><b>Format</b> — <c>"x:&lt;x&gt; y:&lt;y&gt; z:&lt;z&gt;"</c>, the three coordinates rounded to whole Quake
///         units and labelled by axis, right-aligned.</item>
///   <item><b>Placement</b> — bottom-right, on the row above the FPS line (and above the ping line when it's
///         shown), so the readouts stack without overlapping (the panel owns the full viewport rect).</item>
/// </list>
///
/// Like <see cref="FpsPanel"/> it defaults <b>on in debug builds</b> (<see cref="OS.IsDebugBuild"/>) unless the
/// player has explicitly changed the cvar — so a developer always sees their map coordinates without touching a
/// setting, while a shipped release stays clean unless <c>showposition</c> is turned on.
/// </summary>
public partial class PositionPanel : HudPanel
{
    /// <summary>Supplies the local player's current Quake-space origin, or <c>null</c> when there's no live body
    /// (menu / pre-spawn / model viewer). The net layer sets this to read
    /// <see cref="XonoticGodot.Game.Net.ClientNet.PredictedOrigin"/>; null on paths with no local player.</summary>
    public System.Func<NVec3?>? PositionProvider { get; set; }

    /// <summary>
    /// View angles in Quake convention (pitch, yaw, roll), appended to the readout when supplied. Shown
    /// because a position alone cannot reproduce a view: reporting "I see it here" needs the direction too,
    /// and the yaw/pitch pair printed here is exactly what <c>--observe "x y z yaw pitch"</c> takes.
    /// </summary>
    public System.Func<NVec3?>? AnglesProvider { get; set; }

    /// <summary>Always animating — the origin changes continuously while moving, so redraw each frame while shown.</summary>
    public override bool IsDynamic => true;

    public override void _Process(double delta)
    {
        // Self-driven visibility + redraw (this panel owns no player, so it doesn't rely on Hud.SetPlayer).
        bool show = ShowMode() != 0 && PositionProvider is not null;
        if (show != Visible)
            Visible = show;
        // Redraw only when the displayed (rounded) coordinates or layout change — standing still shouldn't churn
        // a redraw every rendered frame (3.2-3).
        if (show && NeedsRedraw())
            QueueRedraw();
    }

    // Last-drawn snapshot for the change gate (alloc-free primitive compare, not a formatted string).
    private int _lastX = int.MinValue, _lastY = int.MinValue, _lastZ = int.MinValue;
    private int _lastYaw = int.MinValue, _lastPitch = int.MinValue;
    private bool _lastHave;
    private int _lastWidth, _lastHeight;

    /// <summary>True only when the drawn values (coordinates and angles, rounded as displayed), the
    /// have-position state, or the viewport size changed since the last draw (3.2-3).</summary>
    public override bool NeedsRedraw()
    {
        NVec3? p = ShowMode() != 0 && PositionProvider is not null ? PositionProvider() : null;
        bool have = p.HasValue;
        int x = have ? Mathf.RoundToInt(p!.Value.X) : 0;
        int y = have ? Mathf.RoundToInt(p!.Value.Y) : 0;
        int z = have ? Mathf.RoundToInt(p!.Value.Z) : 0;

        // The ANGLES are drawn too, so they have to be in the gate. Comparing position alone left the
        // readout stale through any turn that did not also move the camera — which is exactly how a mapper
        // lines up a shot, so the numbers were wrong precisely when they were being read.
        NVec3? a = AnglesProvider?.Invoke();
        int yaw = a.HasValue ? Mathf.RoundToInt(a.Value.Y) : 0;
        int pitch = a.HasValue ? Mathf.RoundToInt(a.Value.X) : 0;

        int w = (int)Size2.X, h = (int)Size2.Y;
        if (have == _lastHave && x == _lastX && y == _lastY && z == _lastZ
            && yaw == _lastYaw && pitch == _lastPitch && w == _lastWidth && h == _lastHeight)
            return false;
        _lastHave = have; _lastX = x; _lastY = y; _lastZ = z;
        _lastYaw = yaw; _lastPitch = pitch;
        _lastWidth = w; _lastHeight = h;
        return true;
    }

    /// <summary>The effective <c>cl_showposition</c> mode: 0 = off, non-zero = show. Reads <c>showposition</c>
    /// (the menu-bound name) then <c>cl_showposition</c>; in a debug build it defaults to 1 unless the player has
    /// changed the cvar from its default (so devs always see it, releases stay opt-in) — mirrors FpsPanel.</summary>
    private static int ShowMode()
    {
        if (Api.Services is null)
            return OS.IsDebugBuild() ? 1 : 0;

        ICvarService cv = Api.Cvars;
        int mode = (int)cv.GetFloat("showposition");
        if (mode == 0)
            mode = (int)cv.GetFloat("cl_showposition");
        if (mode != 0)
            return mode;

        if (OS.IsDebugBuild() && cv is CvarService cs && !cs.IsModified("showposition") && !cs.IsModified("cl_showposition"))
            return 1;
        return 0;
    }

    /// <summary>Whether the ping line currently occupies its row (so we stack above it rather than over it). The
    /// ping readout sits one row above FPS when enabled; mirrors PingPanel's enable gate (no debug default).</summary>
    private static bool PingActive()
    {
        if (Api.Services is null)
            return false;
        ICvarService cv = Api.Cvars;
        return (int)cv.GetFloat("showping") != 0 || (int)cv.GetFloat("cl_showping") != 0;
    }

    protected override void DrawPanel()
    {
        if (ShowMode() == 0 || PositionProvider is null)
            return;

        NVec3? p = PositionProvider();
        if (!p.HasValue)
            return; // no live local body (menu / pre-spawn) — draw nothing rather than a misleading 0 0 0

        NVec3 o = p.Value;
        string text = $"x:{Mathf.RoundToInt(o.X)} y:{Mathf.RoundToInt(o.Y)} z:{Mathf.RoundToInt(o.Z)}";
        if (AnglesProvider?.Invoke() is { } a)
            text += $"  yaw:{Mathf.RoundToInt(a.Y)} pitch:{Mathf.RoundToInt(a.X)}";
        Color color = new(1f, 1f, 1f, 1f);

        // Match FpsPanel's sizing/placement so the readouts share a column and line height; offset up so position
        // sits above the FPS row (row 1) and above the ping row (row 2) when ping is shown.
        int size = (int)Mathf.Clamp(Size2.Y * 0.018f, 12f, 28f);
        float tw = MeasureText(text, size);
        float lineH = size + 4f;
        float x = Size2.X - tw - 4f;          // right-aligned, small inset from the screen edge
        int row = PingActive() ? 3 : 2;       // above fps (row 1), and above ping (row 2) when it's drawn
        float y = Size2.Y - row * lineH;

        // Translucent black backing from the text to the right edge (matches FpsPanel's DrawQ_Fill).
        DrawRect(new Rect2(x - 2f, y, Size2.X - (x - 2f), lineH), new Color(0f, 0f, 0f, 0.5f));
        DrawText(new Vector2(x, y), text, color, size);
    }
}
