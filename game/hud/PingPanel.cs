using Godot;
using VortexArena.Common.Services;

namespace VortexArena.Game.Hud;

/// <summary>
/// Round-trip-latency ("ping") readout — the network sibling of <see cref="FpsPanel"/>, drawn one line above
/// the FPS counter in the bottom-right corner. It mirrors how Darkplaces/Xonotic stacks its info-bar readouts
/// (fps / time / ping) along the bottom edge, but ping here is a modern addition rather than a 1:1 DP port: DP
/// surfaces ping only in the scoreboard, so this introduces a dedicated <c>cl_showping</c> cvar (virtual alias
/// <c>showping</c>, the name the video-settings checkbox binds — DialogSettingsVideo.cs).
///
/// <list type="bullet">
///   <item><b>Source</b> — the value comes from <see cref="PingProvider"/>, which the net layer wires to
///         <see cref="VortexArena.Game.Net.ClientNet.PingMs"/> (ENet's smoothed round-trip estimate to the
///         server). A negative value means "not connected / no net path" and the panel draws nothing.</item>
///   <item><b>Format</b> — <c>"%4i ms"</c>, color-banded green→yellow→red by latency like the scoreboard's
///         ping column, so a glance reads connection quality.</item>
///   <item><b>Placement</b> — bottom-right, right-aligned, on the row directly above the FPS line so the two
///         readouts stack without overlapping (the panel owns the full viewport rect and positions itself).</item>
/// </list>
///
/// Unlike <see cref="FpsPanel"/> this one is <b>off by default</b> (including debug builds): ping reads ~0 on
/// the loopback listen server most testing runs on, so an always-on readout would just show a constant 0 —
/// it's opt-in via the cvar / the settings checkbox, exactly the enable/disable toggle the HUD wants.
/// </summary>
public partial class PingPanel : HudPanel
{
    /// <summary>Supplies the current ping in milliseconds (negative = unknown / not connected). The net layer
    /// sets this to read <see cref="VortexArena.Game.Net.ClientNet.PingMs"/>; null on the offline/demo path.</summary>
    public System.Func<int>? PingProvider { get; set; }

    /// <summary>Always animating — ping changes continuously, so redraw each frame while shown.</summary>
    public override bool IsDynamic => true;

    public override void _Process(double delta)
    {
        bool show = ShowMode() != 0 && PingProvider is not null;
        if (show != Visible)
            Visible = show;
        // Redraw only when the displayed ping integer (or layout) changes — a new RTT measurement arrives far
        // less often than once per rendered frame, so the every-frame redraw was wasted work (3.2-3).
        if (show && NeedsRedraw())
            QueueRedraw();
    }

    private int _lastPing = int.MinValue;
    private int _lastWidth, _lastHeight;

    /// <summary>True only when the drawn ping value or the viewport size changed (3.2-3).</summary>
    public override bool NeedsRedraw()
    {
        int ping = ShowMode() != 0 && PingProvider is not null ? PingProvider() : int.MinValue;
        int w = (int)Size2.X, h = (int)Size2.Y;
        if (ping == _lastPing && w == _lastWidth && h == _lastHeight)
            return false;
        _lastPing = ping; _lastWidth = w; _lastHeight = h;
        return true;
    }

    /// <summary>The effective <c>cl_showping</c> mode: 0 = off, non-zero = show. Reads <c>showping</c> (the
    /// menu-bound name) then <c>cl_showping</c>. Off by default — no debug default-on (ping is ~0 on a listen
    /// server, so showing it unprompted is noise; it's an opt-in like a real ping toggle).</summary>
    private static int ShowMode()
    {
        if (Api.Services is null)
            return 0;
        ICvarService cv = Api.Cvars;
        int mode = (int)cv.GetFloat("showping");
        if (mode == 0)
            mode = (int)cv.GetFloat("cl_showping");
        return mode;
    }

    protected override void DrawPanel()
    {
        if (ShowMode() == 0 || PingProvider is null)
            return;

        int ping = PingProvider();
        if (ping < 0)
            return; // not connected / no measurement yet — draw nothing rather than a misleading 0

        string text = $"{ping,4} ms";
        Color color = PingColor(ping);

        // Match FpsPanel's sizing/placement so the two readouts share a column and line height; offset up by one
        // line (2× lineH) so ping sits directly above the FPS row (FpsPanel draws at Size2.Y - lineH).
        int size = (int)Mathf.Clamp(Size2.Y * 0.018f, 12f, 28f);
        float tw = MeasureText(text, size);
        float lineH = size + 4f;
        float x = Size2.X - tw - 4f;          // right-aligned, small inset from the screen edge
        float y = Size2.Y - 2f * lineH;       // the row above the FPS line

        // Translucent black backing from the text to the right edge (matches FpsPanel's DrawQ_Fill).
        DrawRect(new Rect2(x - 2f, y, Size2.X - (x - 2f), lineH), new Color(0f, 0f, 0f, 0.5f));
        DrawText(new Vector2(x, y), text, color, size);
    }

    /// <summary>Color a ping value green→yellow→red by latency, matching the scoreboard's ping column bands
    /// (ScoreboardPanel.PingColor) so the two read consistently.</summary>
    private static Color PingColor(int ping)
    {
        const int low = 75, med = 200, high = 500;
        Color cLow = new(0f, 1f, 0f, 1f), cMed = new(1f, 1f, 0f, 1f), cHigh = new(1f, 0f, 0f, 1f);
        if (ping < low) return cLow;
        if (ping < med) return cLow.Lerp(cMed, (ping - low) / (float)(med - low));
        if (ping < high) return cMed.Lerp(cHigh, (ping - med) / (float)(high - med));
        return cHigh;
    }
}
