using System;
using Godot;
using VortexArena.Net.Demo;

namespace VortexArena.Game.Hud;

/// <summary>
/// [T63·P2] Demo-replay time-control bar (demo-replay-and-spectator.md §9): a bottom-of-screen strip with a
/// draggable scrub slider, play/pause, a speed selector, and a time readout, driving the demo
/// <see cref="DemoPlayback"/> playhead directly (the local single-viewer case). Visible only in a replay.
///
/// <para>Keybinds (handled here, chosen NOT to collide with the free-fly observer's WASD+mouse): <c>P</c> =
/// pause/play, <c>←</c>/<c>→</c> = seek ∓5 s, <c>[</c>/<c>]</c> = slower/faster, <c>,</c>/<c>.</c> = step one frame,
/// <c>R</c> = smooth rewind. The mouse slider/buttons work whenever the cursor is free (e.g. the in-game menu/console
/// is open); a dedicated "free cursor for replay controls" toggle is a later polish item.</para>
/// </summary>
public partial class ReplayControlBar : Control
{
    /// <summary>The demo playhead this bar drives. Set by the host before the node enters the tree.</summary>
    public DemoPlayback? Playback { get; set; }

    private static readonly float[] SpeedLadder = { 0.25f, 0.5f, 1f, 2f, 4f };
    private const float SeekStep = 5f;        // arrow-key seek, seconds
    private const float FrameStep = 1f / 72f; // comma/period single-frame step (one 72 Hz tick)

    private HSlider _scrub = null!;
    private Label _time = null!;
    private Label _speed = null!;
    private Button _playPause = null!;
    private int _ladderIndex = 2; // 1×
    private bool _syncing;        // guard: distinguish our programmatic slider writes from a user drag

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always; // keep scrubbing alive even when the tree is paused (Esc menu)
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;  // the root is click-through; only the panel below captures the mouse

        var panel = new PanelContainer
        {
            // a centred strip ~12 px off the bottom
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 1f, AnchorBottom = 1f,
            OffsetLeft = -460, OffsetRight = 460, OffsetTop = -64, OffsetBottom = -12,
            MouseFilter = MouseFilterEnum.Stop,
        };
        var bg = new StyleBoxFlat { BgColor = new Color(0f, 0f, 0f, 0.55f), CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6 };
        bg.SetContentMarginAll(8);
        panel.AddThemeStyleboxOverride("panel", bg);
        AddChild(panel);

        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);
        panel.AddChild(row);

        _time = new Label { Text = "0:00 / 0:00", CustomMinimumSize = new Vector2(120, 0), VerticalAlignment = VerticalAlignment.Center };
        row.AddChild(_time);

        _scrub = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.001, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        _scrub.ValueChanged += OnScrubChanged;
        row.AddChild(_scrub);

        row.AddChild(MakeButton("◀◀", Rewind, "Rewind (R)"));
        _playPause = MakeButton("❚❚", TogglePause, "Play / Pause (P)");
        row.AddChild(_playPause);
        row.AddChild(MakeButton("«", Slower, "Slower ([)"));
        _speed = new Label { Text = "1×", CustomMinimumSize = new Vector2(56, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        row.AddChild(_speed);
        row.AddChild(MakeButton("»", Faster, "Faster (])"));

        Visible = Playback is not null;
    }

    private static Button MakeButton(string text, Action onPressed, string tooltip)
    {
        var b = new Button { Text = text, TooltipText = tooltip, CustomMinimumSize = new Vector2(44, 36) };
        b.Pressed += onPressed;
        return b;
    }

    public override void _Process(double delta)
    {
        if (Playback is null) return;

        double dur = Math.Max(0.001, Playback.DurationSeconds);
        _syncing = true;
        if (Math.Abs(_scrub.MaxValue - dur) > 1e-6) _scrub.MaxValue = dur;
        _scrub.Value = Math.Clamp(Playback.PlayheadSeconds, 0, dur);
        _syncing = false;

        _time.Text = $"{Fmt(Playback.PlayheadSeconds)} / {Fmt(Playback.DurationSeconds)}";
        _speed.Text = SpeedText(Playback);
        _playPause.Text = Playback.Paused ? "▶" : "❚❚";
    }

    private void OnScrubChanged(double value)
    {
        if (_syncing || Playback is null) return; // ignore our own _Process writes — only react to user drags
        Playback.Seek((float)value);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Playback is null || @event is not InputEventKey k || !k.Pressed || k.Echo) return;
        bool handled = true;
        switch (k.Keycode)
        {
            case Key.P: TogglePause(); break;
            case Key.Left: Seek(-SeekStep); break;
            case Key.Right: Seek(SeekStep); break;
            case Key.Bracketleft: Slower(); break;
            case Key.Bracketright: Faster(); break;
            case Key.R: Rewind(); break;
            case Key.Comma: StepFrame(-FrameStep); break;
            case Key.Period: StepFrame(FrameStep); break;
            default: handled = false; break;
        }
        if (handled) GetViewport().SetInputAsHandled();
    }

    private void TogglePause()
    {
        if (Playback is null) return;
        Playback.Paused = !Playback.Paused;
        if (!Playback.Paused && Playback.Speed < 0f)
            Playback.Speed = SpeedLadder[_ladderIndex]; // resuming from a rewind resumes FORWARD play
    }

    private void Slower()
    {
        if (Playback is null) return;
        _ladderIndex = Math.Max(0, _ladderIndex - 1);
        Playback.Speed = SpeedLadder[_ladderIndex];
    }

    private void Faster()
    {
        if (Playback is null) return;
        _ladderIndex = Math.Min(SpeedLadder.Length - 1, _ladderIndex + 1);
        Playback.Speed = SpeedLadder[_ladderIndex];
    }

    private void Rewind()
    {
        if (Playback is null) return;
        Playback.Speed = -1f;   // smooth rewind (§3): the playhead runs backward, clients lerp the reverse motion
        Playback.Paused = false;
    }

    private void Seek(float deltaSeconds)
    {
        if (Playback is not null) Playback.Seek(Playback.PlayheadSeconds + deltaSeconds);
    }

    private void StepFrame(float deltaSeconds)
    {
        if (Playback is null) return;
        Playback.Paused = true; // frame-stepping implies paused
        Playback.Seek(Playback.PlayheadSeconds + deltaSeconds);
    }

    private static string SpeedText(DemoPlayback playback)
    {
        if (playback.Paused) return "❚❚";
        float speed = playback.Speed;
        if (speed < 0f) return $"◀{(-speed):0.##}×";
        return $"{speed:0.##}×";
    }

    private static string Fmt(float seconds)
    {
        if (seconds < 0f) seconds = 0f;
        int total = (int)seconds;
        return $"{total / 60}:{total % 60:00}";
    }
}
