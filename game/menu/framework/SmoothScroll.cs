using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Gameplay;

namespace VortexArena.Game.Menu;

/// <summary>
/// The Xonotic menu's scroll feel, ported to Godot's scrollable controls — C# successor to the scroll
/// half of <c>ListBox_draw</c> (qcsrc/menu/item/listbox.qc:336-357).
///
/// <para>A QC listbox never jumps: every scroll input moves <c>scrollPosTarget</c>, and each frame the drawn
/// <c>scrollPos</c> is eased toward it with an exponential average whose time constant is the
/// <c>menu_scroll_averaging_time</c> autocvar (0.16 s, or 0.06 s while the scrollbar is being dragged). The
/// formula is framerate-independent by construction:</para>
/// <code>
///   f = exp(-frametime / averaging_time)
///   scrollPos = scrollPos * f + scrollPosTarget * (1 - f)
/// </code>
/// <para>Godot's <c>ItemList</c>/<c>Tree</c>/<c>ScrollContainer</c> instead snap their scroll offset by a fixed
/// number of lines per wheel notch. <see cref="Attach"/> restores the Base behaviour on any of them without
/// subclassing: it parents a small helper node to the control, swallows wheel events over it in
/// <c>_Input</c> (which runs before GUI input, so the control never sees them and never snaps), moves a
/// target offset instead, and eases the real scrollbar toward it every frame.</para>
///
/// <para>The wheel step is Base's, too: <c>ListBox_keyDown</c> moves the target by <b>0.5</b> — and QC
/// scroll positions are measured in <em>window heights</em>, so one notch is half a visible page rather than
/// Godot's three lines.</para>
///
/// <para>Lists that draw themselves (<see cref="MenuListBox"/>) do not use this; they run the same easing
/// directly through <see cref="Advance"/>, which is the shared implementation of the formula above.</para>
/// </summary>
public partial class SmoothScroll : Node
{
    /// <summary>Every live helper, so a wheel event over nested scrollables can pick the innermost one.</summary>
    private static readonly List<SmoothScroll> Live = new();

    private readonly Control _target;
    private VScrollBar? _bar;

    /// <summary>Where the view is easing to (in the scrollbar's own units).</summary>
    private double _wanted;

    /// <summary>The last value we wrote, so an outside change (drag, keyboard, ensure-visible) is detectable.</summary>
    private double _written = double.NaN;

    private SmoothScroll(Control target) => _target = target;

    /// <summary>
    /// Give <paramref name="target"/> the Xonotic scroll feel. Accepts any control that owns a vertical
    /// scrollbar — <see cref="ItemList"/>, <see cref="Tree"/>, <see cref="ScrollContainer"/>. Safe to call
    /// before the control enters the tree, and safe to call twice (the second call is a no-op).
    /// </summary>
    public static void Attach(Control target)
    {
        if (target is null)
            return;
        foreach (Node child in target.GetChildren())
            if (child is SmoothScroll)
                return;
        target.AddChild(new SmoothScroll(target) { Name = "SmoothScroll" });
    }

    /// <summary>
    /// The <c>menu_scroll_averaging_time</c> family: how long the view takes to catch up with the target.
    /// <paramref name="dragging"/> selects the shorter constant the QC uses while the scrollbar grabber is
    /// held, so dragging tracks the cursor instead of lagging behind it.
    /// </summary>
    public static float AveragingTime(bool dragging) => MenuState.Cvars.GetFloat(
        dragging ? "menu_scroll_averaging_time_pressed" : "menu_scroll_averaging_time");

    /// <summary>
    /// One frame of the QC easing: move <paramref name="pos"/> toward <paramref name="target"/> over
    /// <paramref name="averagingTime"/> seconds and snap when within <paramref name="epsilon"/>. An
    /// averaging time of 0 means "no smoothing" (the QC guards the same way). Shared with
    /// <see cref="MenuListBox"/> so both scroll paths are provably the same curve.
    /// </summary>
    public static double Advance(double pos, double target, double dt, float averagingTime, double epsilon)
        => ServerListInfo.AdvanceScroll(pos, target, dt, averagingTime, epsilon);

    public override void _Ready()
    {
        Live.Add(this);
        _bar = FindScrollBar(_target);
        if (_bar is not null)
            _wanted = _written = _bar.Value;
    }

    public override void _ExitTree() => Live.Remove(this);

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mb || !mb.Pressed)
            return;
        int dir = mb.ButtonIndex switch
        {
            MouseButton.WheelUp => -1,
            MouseButton.WheelDown => +1,
            _ => 0,
        };
        if (dir == 0 || !Innermost(mb.GlobalPosition))
            return;

        VScrollBar? bar = Bar();
        if (bar is null || bar.MaxValue - bar.Page <= bar.MinValue)
            return; // nothing to scroll — let the event through to whatever is behind us

        // ListBox_keyDown K_MWHEELUP/DOWN: ±0.5, in units of one visible page.
        Aim(bar, _wanted + dir * 0.5 * bar.Page);
        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        VScrollBar? bar = Bar();
        if (bar is null || !_target.IsVisibleInTree())
            return;

        // Anything that moved the bar behind our back — a grabber drag, a keyboard selection change, a
        // scroll-to-item — becomes the new truth. (This is also what makes dragging feel immediate, which is
        // what the QC's much shorter menu_scroll_averaging_time_pressed constant is for.)
        if (double.IsNaN(_written) || Math.Abs(bar.Value - _written) > 0.5)
        {
            _wanted = _written = bar.Value;
            return;
        }
        if (bar.Value == _wanted)
            return;

        bar.Value = Advance(bar.Value, _wanted, delta, AveragingTime(dragging: false), epsilon: 0.5);
        _written = bar.Value; // re-read: the bar clamps, and we must not read that back as an outside change
    }

    /// <summary>Clamp and store a new scroll destination.</summary>
    private void Aim(VScrollBar bar, double value)
        => _wanted = Math.Clamp(value, bar.MinValue, Math.Max(bar.MinValue, bar.MaxValue - bar.Page));

    /// <summary>Re-resolve the scrollbar if the control rebuilt it (Tree/ItemList do on some layout changes).</summary>
    private VScrollBar? Bar()
    {
        if (_bar is not null && GodotObject.IsInstanceValid(_bar))
            return _bar;
        _bar = FindScrollBar(_target);
        _written = double.NaN;
        return _bar;
    }

    /// <summary>
    /// True when the cursor is over this helper's control and no other smooth-scrolled control nested more
    /// deeply also contains it — so a list inside a scrolling settings tab consumes the wheel itself.
    /// </summary>
    private bool Innermost(Vector2 globalPos)
    {
        if (!Contains(this, globalPos))
            return false;
        int depth = Depth(_target);
        foreach (SmoothScroll other in Live)
        {
            if (other == this || !Contains(other, globalPos))
                continue;
            if (Depth(other._target) > depth)
                return false;
        }
        return true;
    }

    private static bool Contains(SmoothScroll s, Vector2 globalPos)
        => GodotObject.IsInstanceValid(s._target)
           && s._target.IsVisibleInTree()
           && s._target.GetGlobalRect().HasPoint(globalPos);

    private static int Depth(Node n)
    {
        int d = 0;
        for (Node? p = n.GetParent(); p is not null; p = p.GetParent())
            d++;
        return d;
    }

    /// <summary>
    /// The control's vertical scrollbar. <see cref="ScrollContainer"/> exposes one directly; ItemList and
    /// Tree own theirs as internal children, which is why this walks the tree rather than using a typed
    /// accessor (whose availability differs across the controls).
    /// </summary>
    private static VScrollBar? FindScrollBar(Node root)
    {
        if (root is ScrollContainer sc)
            return sc.GetVScrollBar();
        foreach (Node child in root.GetChildren(includeInternal: true))
        {
            if (child is VScrollBar bar)
                return bar;
            if (FindScrollBar(child) is { } nested)
                return nested;
        }
        return null;
    }
}
