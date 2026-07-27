using System;

namespace XonoticGodot.Common.Input;

/// <summary>
/// The DP mouse-acceleration + filter cvar family (cl_input.c:401-412), read by <see cref="MouseAccel"/>.
/// Defaults mirror DP's registrations exactly — at stock values every branch is a mathematical no-op
/// (m_accelerate 1 = linear disabled, strengths 0 = power/natural off, m_filter 0 = no smoothing), so the
/// default path is bit-identical to applying the raw deltas.
/// </summary>
public struct MouseAccelParams
{
    public bool  MFilter;                 // m_filter (0): average the current + previous frame's (post-accel) deltas
    public float Accelerate;              // m_accelerate (1): linear factor; 0 disables the WHOLE accel block (DP gate is > 0)
    public float AccelerateMinSpeed;      // m_accelerate_minspeed (5000 px/s): below → no linear accel
    public float AccelerateMaxSpeed;      // m_accelerate_maxspeed (10000 px/s): above → full linear accel
    public float AccelerateFilter;        // m_accelerate_filter (0 s): averagespeed lowpass constant
    public float PowerOffset;             // m_accelerate_power_offset (0 px/ms)
    public float Power;                   // m_accelerate_power (2)
    public float PowerSensCap;            // m_accelerate_power_senscap (0 = unbounded)
    public float PowerStrength;           // m_accelerate_power_strength (0 = off)
    public float NaturalStrength;         // m_accelerate_natural_strength (0 = off)
    public float NaturalAccelSensCap;     // m_accelerate_natural_accelsenscap (0)
    public float NaturalOffset;           // m_accelerate_natural_offset (0 px/ms)

    /// <summary>DP registration defaults (cl_input.c:401-412).</summary>
    public static MouseAccelParams DpDefaults => new()
    {
        MFilter = false,
        Accelerate = 1f,
        AccelerateMinSpeed = 5000f,
        AccelerateMaxSpeed = 10000f,
        AccelerateFilter = 0f,
        PowerOffset = 0f,
        Power = 2f,
        PowerSensCap = 0f,
        PowerStrength = 0f,
        NaturalStrength = 0f,
        NaturalAccelSensCap = 0f,
        NaturalOffset = 0f,
    };
}

/// <summary>
/// Verbatim port of DP's per-frame mouse pipeline (cl_input.c CL_Input, lines 550-662): the m_accelerate
/// block (linear slope → Quake-Live-style power → natural), then the m_filter two-frame average. Holds the
/// cross-frame state DP keeps in statics (<c>averagespeed</c>, <c>old_mouse_x/y</c>). The caller accumulates
/// raw deltas over the frame (DP's <c>in_mouse_x/y</c>) and calls <see cref="Apply"/> ONCE per render frame —
/// including zero-input frames, which still decay <c>averagespeed</c> through the lowpass and drain the
/// m_filter tail (DP runs the block unconditionally). The returned deltas are then scaled by
/// m_yaw/m_pitch × sensitivity × sensitivityscale at the view-angle application site (NetGame.FlushMouseLook).
/// </summary>
public sealed class MouseAccel
{
    private float _averageSpeed; // DP `static float averagespeed` (cl_input.c:555)
    private float _oldX, _oldY;  // DP `old_mouse_x/y` — the previous frame's POST-accel deltas (filter input)

    /// <summary>Reset the cross-frame state (mouse recapture / level change — DP's cl_ignoremousemoves zeroes old_mouse).</summary>
    public void Reset() { _averageSpeed = 0f; _oldX = 0f; _oldY = 0f; }

    /// <param name="sensitivity">The raw `sensitivity` cvar — the power branch divides by it (DP applies power
    /// accel in absolute-sensitivity units, then the later sensitivity multiply restores it).</param>
    public (float X, float Y) Apply(float dx, float dy, float realFrameTime, in MouseAccelParams p, float sensitivity)
    {
        // apply m_accelerate if it is on (cl_input.c:551 — `> 0`: m_accelerate 0 skips the WHOLE block,
        // power/natural included, and freezes averagespeed; exactly DP's gate)
        if (p.Accelerate > 0f)
        {
            float deltaDist = MathF.Sqrt(dx * dx + dy * dy);
            float speed = deltaDist / MathF.Max(realFrameTime, 1e-6f); // px/s (DP divides by cl.realframetime)
            float f = p.AccelerateFilter > 0f
                ? System.Math.Clamp(realFrameTime / p.AccelerateFilter, 0f, 1f)
                : 1f;
            _averageSpeed = speed * f + _averageSpeed * (1f - f);

            // linear slope acceleration ("ripped in spirit from many classic mouse driver implementations")
            if (p.Accelerate != 1f)
            {
                float mi = MathF.Max(1f, p.AccelerateMinSpeed);
                float ma = MathF.Max(p.AccelerateMinSpeed + 1f, p.AccelerateMaxSpeed);
                if (_averageSpeed <= mi)
                    f = 1f;
                else if (_averageSpeed >= ma)
                    f = p.Accelerate;
                else
                    f = (_averageSpeed - mi) / (ma - mi) * (p.Accelerate - 1f) + 1f;
                dx *= f;
                dy *= f;
            }

            // Quake Live-style power acceleration (REPLACES sensitivity → divide by it so the later multiply restores)
            if (p.PowerStrength != 0f)
            {
                float accelsens = 1f;
                float adjustedSpeedPxMs = (_averageSpeed * 0.001f - p.PowerOffset) * p.PowerStrength;
                float invSensitivity = 1f / MathF.Max(sensitivity, 1e-6f);
                if (adjustedSpeedPxMs > 0f)
                {
                    if (p.Power > 1f)
                        accelsens += MathF.Exp((p.Power - 1f) * MathF.Log(adjustedSpeedPxMs)) * invSensitivity;
                    else
                        accelsens += invSensitivity; // the limit of the then-branch for m_accelerate_power → 1
                }
                if (p.PowerSensCap > 0f && accelsens > p.PowerSensCap * invSensitivity)
                    accelsens = p.PowerSensCap * invSensitivity; // senscap is in absolute sensitivity units
                dx *= accelsens;
                dy *= accelsens;
            }

            // natural acceleration: sensitivity eases toward accelsenscap × base along an exponential curve
            if (p.NaturalStrength > 0f && p.NaturalAccelSensCap >= 0f)
            {
                float accelsens = 1f;
                float adjustedSpeedPxMs = _averageSpeed * 0.001f - p.NaturalOffset;
                if (adjustedSpeedPxMs > 0f && p.NaturalAccelSensCap != 1f)
                {
                    float adjustedCap = p.NaturalAccelSensCap - 1f;
                    accelsens += adjustedCap - adjustedCap
                        * MathF.Exp(-(adjustedSpeedPxMs * p.NaturalStrength / MathF.Abs(adjustedCap)));
                }
                dx *= accelsens;
                dy *= accelsens;
            }
        }

        // apply m_filter if it is on (cl_input.c:653-661): average with the PREVIOUS frame's post-accel deltas
        float outX = dx, outY = dy;
        if (p.MFilter)
        {
            outX = (dx + _oldX) * 0.5f;
            outY = (dy + _oldY) * 0.5f;
        }
        _oldX = dx;
        _oldY = dy;
        return (outX, outY);
    }
}
