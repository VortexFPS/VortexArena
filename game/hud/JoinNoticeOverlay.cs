using System;
using Godot;
using VortexArena.Net;

namespace VortexArena.Game.Hud;

/// <summary>The timed server join notice shown above the ordinary HUD.</summary>
public sealed partial class JoinNoticeOverlay : Control
{
    private static readonly StringName Title = "Server notices:";
    private string[] _lines = Array.Empty<string>();
    private TimedJoinNotice _notice;
    private int _secondsLeft;

    public Func<float>? ServerTime { get; init; }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Resized += QueueRedraw;
    }

    public void ShowNotice(TimedJoinNotice notice)
    {
        _notice = notice;
        _secondsLeft = notice.RemainingSeconds(Now);
        if (_secondsLeft == 0)
        {
            ClearNotice();
            return;
        }
        _lines = notice.Notice.Lines();
        Show();
        QueueRedraw();
    }

    private float Now => ServerTime?.Invoke() ?? 0f;

    public void ClearNotice()
    {
        if (Visible)
            Hide();
        _lines = Array.Empty<string>();
        _notice = default;
    }

    public override void _Process(double delta)
    {
        if (!Visible)
            return;

        int seconds = _notice.RemainingSeconds(Now);
        if (seconds == 0)
            ClearNotice();
        else if (_secondsLeft != seconds)
        {
            _secondsLeft = seconds;
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        if (_lines.Length == 0)
            return;

        Vector2 size = GetViewportRect().Size;
        if (size.X <= 80f || size.Y <= 80f)
            return;

        DrawRect(new Rect2(new Vector2(30, 30), size - new Vector2(60, 60)), new Color(0, 0, 0, 0.5f));
        DrawRect(new Rect2(new Vector2(40, 40), size - new Vector2(80, 80)), new Color(0.5f, 0.5f, 0.5f, 0.5f));
        Font font = ThemeDB.FallbackFont;
        DrawString(font, new Vector2(50, 80), Tr(Title), fontSize: 32, modulate: Colors.Red);
        DrawString(font, new Vector2(50, 108), _secondsLeft.ToString(), fontSize: 16, modulate: Colors.Yellow);
        float y = 108;
        foreach (string line in _lines)
        {
            if (y > size.Y - 40f)
                break;
            float x = 98;
            foreach (HudText.Run run in HudText.Parse(line, Colors.White))
            {
                float width = size.X - 40f - x;
                if (width <= 0f)
                    break;
                DrawString(font, new Vector2(x, y), run.Text, width: width, fontSize: 16, modulate: run.Color);
                x += font.GetStringSize(run.Text, fontSize: 16).X;
            }
            y += 20;
        }
    }
}
