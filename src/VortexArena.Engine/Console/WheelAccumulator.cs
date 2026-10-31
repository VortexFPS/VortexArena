using System;

namespace VortexArena.Engine.Console;

/// <summary>
/// Turns mouse-wheel motion into whole wheel clicks, the way DarkPlaces receives it: SDL2 adds up the wheel
/// delta and reports only whole notches (SDL_SendMouseWheel's accumulated_wheel_y, reset when the direction
/// flips), and vid_sdl.c sends one MWHEELUP/MWHEELDOWN press and release per notch.
///
/// <para>Godot instead sends one wheel event per OS message, carrying the motion as a <c>factor</c> (on Windows
/// <c>|delta| / 120</c>). A standard wheel sends 1.0 per notch, so nothing changes there. A high-resolution or
/// free-spinning wheel sends fractions (0.25, …) and, read one event per click, fired <c>weapnext</c> several
/// times per notch; a fast flick can also pack 2.0 into one event, which fired it once.</para>
/// </summary>
public sealed class WheelAccumulator
{
    private float _accumulated; // > 0 up, < 0 down

    /// <summary>Feed one wheel event; returns how many whole clicks it completes in that direction (often 0).
    /// A <paramref name="factor"/> of 0 or less (a platform that reports none) counts as one click.</summary>
    public int Clicks(bool up, float factor)
    {
        float amount = factor > 0f && float.IsFinite(factor) ? factor : 1f;
        if (up ? _accumulated < 0f : _accumulated > 0f)
            _accumulated = 0f; // direction changed: the other way's partial notch is dropped, as SDL does
        _accumulated += up ? amount : -amount;
        // A small tolerance, so 0.1 * 10 still makes a click despite float rounding.
        int clicks = (int)MathF.Floor(MathF.Abs(_accumulated) + 1e-4f);
        if (clicks > 0)
            _accumulated -= up ? clicks : -clicks;
        if (MathF.Abs(_accumulated) < 1e-4f)
            _accumulated = 0f;
        return clicks;
    }

    /// <summary>Drop any partial notch.</summary>
    public void Reset() => _accumulated = 0f;
}
