using System;

namespace VortexArena.Common.Services;

/// <summary>
/// The QC <c>trace_*</c> globals as a <c>.touch</c> function sees them: DarkPlaces' SV_Impact sets them from the
/// movement trace that produced the touch (VM_SetTraceGlobals) before calling the touch functions, and QC touch
/// code reads them — notably <c>trace_dphitq3surfaceflags &amp; Q3SURFACEFLAG_NOIMPACT</c> in
/// <c>SUB_NoImpactCheck</c>, which is how a projectile (and the grappling hook) vanishes on sky instead of
/// impacting it. The port's touch callbacks take (self, other) only, so the engine's impact dispatch publishes the
/// trace here for the duration of the touch calls. Outside a movement impact (a trigger overlap, a direct
/// <c>Touch</c> call) <see cref="TryGet"/> reports false, the analogue of stale globals a QC caller must not trust.
/// </summary>
public static class TouchTrace
{
    [ThreadStatic] private static TraceResult _current;
    [ThreadStatic] private static bool _valid;

    /// <summary>The trace of the movement impact whose touch functions are running, if any.</summary>
    public static bool TryGet(out TraceResult trace)
    {
        trace = _current;
        return _valid;
    }

    /// <summary>Publish <paramref name="trace"/> for the touch calls that follow; dispose the returned scope to
    /// restore the previous value (touches can nest when a touch moves another entity).</summary>
    public static Scope Begin(in TraceResult trace)
    {
        var scope = new Scope(_current, _valid);
        _current = trace;
        _valid = true;
        return scope;
    }

    /// <summary>Restores the trace that was current before <see cref="Begin"/>.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly TraceResult _prev;
        private readonly bool _prevValid;
        internal Scope(TraceResult prev, bool prevValid) { _prev = prev; _prevValid = prevValid; }
        public void Dispose()
        {
            _current = _prev;
            _valid = _prevValid;
        }
    }
}
