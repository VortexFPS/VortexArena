using Godot;

namespace VortexArena.Game.Hud;

/// <summary>
/// Engine-info HUD panel (#13) — the faithful C# port of Xonotic's CSQC <c>HUD_EngineInfo</c>
/// (Base/.../qcsrc/client/hud/panel/engineinfo.qc), distinct from the Darkplaces-style <see cref="FpsPanel"/>
/// overlay (which ports the engine's <c>Sbar_ShowFPS</c> and is gated on <c>cl_showfps</c>). This is a regular
/// positioned HUD panel: it draws <c>"FPS: %.*f"</c> inside its own <c>hud_panel_engineinfo_*</c> rect, gated on
/// the <c>hud_panel_engineinfo</c> enable cvar, and reproduces engineinfo.qc's two measurement modes:
///
/// <list type="bullet">
///   <item><b>moving average</b> (default, <c>hud_panel_engineinfo_fps_movingaverage</c> 1): a 3-frame
///         frametime average fed into an exponential moving average of the FPS, with a big-jump instant-update
///         reset (<c>_instantupdate_threshold</c>) and a configurable <c>_weight</c> — engineinfo.qc:43-58.</item>
///   <item><b>fixed window</b> (<c>_fps_movingaverage</c> 0): frames counted over a <c>_fps_time</c>-second
///         window, recomputed as <c>framecounter / elapsed</c> — engineinfo.qc:59-68.</item>
/// </list>
///
/// The reading is color-banded by <see cref="HudPanel.NumColor"/> (QC <c>HUD_Get_Num_Color(prevfps, 100, true)</c>)
/// and printed with <c>_fps_decimals</c> decimal places. Cvar defaults mirror engineinfo.qh:4-10.
/// </summary>
public partial class EngineInfoPanel : HudPanel
{
    // engineinfo.qh defaults.
    private const float DefaultFpsTime = 0.1f;
    private const float DefaultDecimals = 0f;
    private const float DefaultMovingAverage = 1f;
    private const float DefaultMovingAverageWeight = 0.1f;
    private const float DefaultInstantUpdateThreshold = 0.5f;

    // engineinfo.qc module statics (prevfps / prevfps_time / framecounter / frametimeavg*).
    private float _prevFps;
    private float _prevFpsTime;
    private int _frameCounter;
    private float _frameTimeAvg;
    private float _frameTimeAvg1; // 1 frame ago
    private float _frameTimeAvg2; // 2 frames ago

    private double _clock;        // monotonic real time — the GETTIME_FRAMESTART analogue (host.realtime)

    /// <summary>The panel's id is "engineinfo" so its cvars are <c>hud_panel_engineinfo_*</c> (the registered
    /// Xonotic panel #13 slot in <see cref="HudLayoutDefaults"/>), NOT the DP "fps" overlay's slot.</summary>
    public override string PanelId => "engineinfo";

    /// <summary>The FPS reading updates continuously, so the panel animates (redraw each frame while shown).</summary>
    public override bool IsDynamic => true;

    public override void _Process(double delta)
    {
        // Advance the measurement every rendered frame regardless of visibility so the average is warm when the
        // panel is toggled on (matches engineinfo.qc which only runs when drawn, but the cost here is trivial and
        // avoids a cold first reading). The clock is the GETTIME_FRAMESTART analogue.
        _clock += delta;
        UpdateFps((float)_clock);
        base._Process(delta);
    }

    /// <summary>engineinfo.qc:42-68 — recompute <c>prevfps</c> via the moving-average or fixed-window path.</summary>
    private void UpdateFps(float currentTime)
    {
        if (MovingAverageEnabled())
        {
            // engineinfo.qc:45-57.
            float currentFrameTime = currentTime - _prevFpsTime;
            _frameTimeAvg = (_frameTimeAvg + _frameTimeAvg1 + _frameTimeAvg2 + currentFrameTime) * 0.25f;
            _frameTimeAvg2 = _frameTimeAvg1;
            _frameTimeAvg1 = _frameTimeAvg;

            if (currentFrameTime > 0.0001f) // filter out insane values (engineinfo.qc:50)
            {
                float threshold = CvarF("fps_movingaverage_instantupdate_threshold", DefaultInstantUpdateThreshold);
                if (Mathf.Abs(_prevFps - (1f / _frameTimeAvg)) > _prevFps * threshold)
                    _prevFps = 1f / currentFrameTime; // big jump → snap instantly (engineinfo.qc:52-53)
                float weight = CvarF("fps_movingaverage_weight", DefaultMovingAverageWeight);
                _prevFps = (1f - weight) * _prevFps + weight * (1f / _frameTimeAvg); // engineinfo.qc:55
            }
            _prevFpsTime = currentTime;
        }
        else
        {
            // engineinfo.qc:61-67.
            ++_frameCounter;
            float window = CvarF("fps_time", DefaultFpsTime);
            if (currentTime - _prevFpsTime > window)
            {
                _prevFps = _frameCounter / (currentTime - _prevFpsTime);
                _frameCounter = 0;
                _prevFpsTime = currentTime;
            }
        }
    }

    private bool MovingAverageEnabled() => CvarF("fps_movingaverage", DefaultMovingAverage) != 0f;

    protected override void DrawPanel()
    {
        // QC: bail unless _hud_configure or hud_panel_engineinfo. Cfg.Enabled tracks hud_panel_engineinfo (the
        // per-panel enable cvar, default 0 per the luma table) — off by default like Base.
        if (!Cfg.Enabled)
            return;

        DrawBackground();

        // QC HUD_Get_Num_Color(prevfps, 100, true): tint the reading by how far below 100 fps it is.
        Color color = NumColor(_prevFps, 100f);
        int decimals = (int)CvarF("fps_decimals", DefaultDecimals);
        if (decimals < 0) decimals = 0;
        string text = $"FPS: {_prevFps.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture)}";

        // drawstring_aspect: fit the line within the panel rect (centered vertically), like the QC aspect draw.
        int size = (int)Mathf.Clamp(Size2.Y * 0.6f, 8f, Cfg.FontSize * 2f);
        float tw = MeasureText(text, size);
        float x = Mathf.Max(Cfg.Padding, (Size2.X - tw) * 0.5f);
        float y = Mathf.Max(0f, (Size2.Y - size) * 0.5f);
        DrawText(new Vector2(x, y), text, color, size);
    }
}
