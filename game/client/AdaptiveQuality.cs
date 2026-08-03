// Adaptive quality feedback loop — the port of DarkPlaces' cl_minfps machinery
// (darkplaces/cl_screen.c:2130-2213, published as r_refdef.view.quality).
//
// WHY THIS EXISTS, and why our consumers differ from DP's.
//
// DP measures render time, EMA-filters it, nudges a quality scalar toward the cl_minfps target with
// one-sided hysteresis and a clamped per-frame step, then feeds that scalar to LOD selection, particle
// draw distance and offsetmapping. Those are GPU/fillrate-shaped consumers, because that is what DP was
// usually short of.
//
// This port is CPU-bound with the GPU ~84% idle (planning/perf-deep-dive-2026-08-02.md), so the same loop
// has to drive CPU-shaped costs or it does nothing. Measured evidence for the choice of consumers:
//   - LOD is deliberately NOT a consumer here: the stock LOD rigs have IDENTICAL bone counts (erebus
//     60/60/60), so they cut vertices — GPU work we already have in surplus — and cut neither the
//     per-bone pose interop nor draw calls. Wiring it would buy ~0 and add mid-match model-swap churn.
//   - particles.cpu leads ~19% of stormkeep frames and owns its worst hitches, and the DP-faithful sim is
//     pure CPU (per-particle integrate + world traces). Spawning fewer particles under load is the single
//     largest CPU lever that degrades gracefully.
//
// Default OFF (cl_minfps 0), exactly like DP: this trades fidelity for frame time, so it is a setting the
// player opts into, not something that silently changes what the game looks like. The Video dialog's
// existing cl_minfps slider drives it.
using Godot;
using VortexArena.Common.Services;

namespace VortexArena.Game.Client;

/// <summary>
/// The frame-time feedback controller: measures how the frame budget is actually going and publishes a
/// 0.25..1 quality scalar that CPU-heavy subsystems scale their work by. <see cref="Scale"/> is 1 (no
/// effect) whenever the loop is disabled, so every consumer can multiply unconditionally.
/// </summary>
public static class AdaptiveQuality
{
    /// <summary>Current quality scalar, 0.25..1. 1 = full fidelity (also the value while disabled).</summary>
    public static float Scale { get; private set; } = 1f;

    /// <summary>True while the loop is actively steering (cl_minfps &gt; 0) — for diagnostics/HUD.</summary>
    public static bool Active { get; private set; }

    private static double _emaMs;          // EMA-filtered frame time (ms)
    private static float _lastReported = 1f;

    /// <summary>Register the DP cvar set (names + defaults from cl_screen.c). Idempotent.</summary>
    public static void RegisterDefaults(ICvarService c)
    {
        c.Register("cl_minfps", "0", CvarFlags.Save);              // 0 = disabled (DP default)
        c.Register("cl_minfps_fade", "0.2", CvarFlags.Save);       // EMA weight for the frame-time filter
        c.Register("cl_minfps_qualitymin", "0.25", CvarFlags.Save);
        c.Register("cl_minfps_qualitymax", "1", CvarFlags.Save);
        c.Register("cl_minfps_qualitystepmax", "0.1", CvarFlags.Save);
        c.Register("cl_minfps_qualityhysteresis", "0.05", CvarFlags.Save);
    }

    /// <summary>
    /// Advance one frame. <paramref name="deltaSec"/> is the real frame interval (the thing the player
    /// feels), not a scaled game delta. Call once per frame, before the consumers read <see cref="Scale"/>.
    /// </summary>
    public static void Update(double deltaSec, ICvarService? cv)
    {
        if (cv is null)
            return;
        float minFps = cv.GetFloat("cl_minfps");
        if (minFps <= 0f || deltaSec <= 0.0 || double.IsNaN(deltaSec) || double.IsInfinity(deltaSec))
        {
            // Disabled (or a garbage delta — a stalled/paused frame must never drive the controller):
            // release to full quality and forget the filter so re-enabling starts clean.
            Active = false;
            Scale = 1f;
            _emaMs = 0.0;
            return;
        }
        Active = true;

        double ms = deltaSec * 1000.0;
        // Ignore single catastrophic frames (a load hitch, an alt-tab): they say nothing about steady-state
        // headroom, and letting them into the filter would slam quality down for seconds afterwards.
        if (ms > 250.0)
            return;
        double fade = Mathf.Clamp(cv.GetFloat("cl_minfps_fade"), 0.01f, 1f);
        _emaMs = _emaMs <= 0.0 ? ms : _emaMs + (ms - _emaMs) * fade;

        double targetMs = 1000.0 / minFps;
        float qMin = cv.GetFloat("cl_minfps_qualitymin");
        float qMax = cv.GetFloat("cl_minfps_qualitymax");
        float stepMax = Mathf.Max(cv.GetFloat("cl_minfps_qualitystepmax"), 0.001f);
        float hysteresis = Mathf.Max(cv.GetFloat("cl_minfps_qualityhysteresis"), 0f);

        // Ratio > 1 ⇒ frames are FASTER than the target, so there is headroom to spend on fidelity.
        double ratio = targetMs / _emaMs;

        // One-sided hysteresis (DP cl_screen.c:2173): react immediately when we are missing the target, but
        // only give quality back once we are comfortably inside it. Without this the controller oscillates,
        // because raising quality is itself what pushed the frame time back up.
        float desired;
        if (ratio < 1.0)
            desired = Scale * (float)ratio;                       // over budget → cut now
        else if (ratio > 1.0 + hysteresis)
            desired = Scale * (float)(1.0 + (ratio - 1.0) * 0.5); // under budget with margin → ease back up
        else
            desired = Scale;                                      // inside the dead band → hold

        // Clamp the per-frame movement so quality never visibly snaps, then clamp the range.
        desired = Mathf.Clamp(desired, Scale - stepMax, Scale + stepMax);
        Scale = Mathf.Clamp(desired, Mathf.Min(qMin, qMax), Mathf.Max(qMin, qMax));

        // Breadcrumb on a meaningful move, so a capture can explain why effect density changed mid-session.
        if (Mathf.Abs(Scale - _lastReported) >= 0.1f)
        {
            _lastReported = Scale;
            VortexArena.Common.Diagnostics.Prof.Event(
                $"adaptive quality {Scale:F2} (frame {_emaMs:F1}ms vs {targetMs:F1}ms target)");
        }
    }

    /// <summary>Reset between matches so a new map starts at full fidelity.</summary>
    public static void Reset()
    {
        Scale = 1f;
        _lastReported = 1f;
        _emaMs = 0.0;
        Active = false;
    }
}
