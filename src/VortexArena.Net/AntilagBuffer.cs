using System.Numerics;

namespace XonoticGodot.Net;

/// <summary>
/// Fixed-size ring of timestamped position samples for ONE rewindable entity — the server-side
/// lag-compensation (antilag) history, ported from <c>server/antilag.qc</c>
/// (<c>antilag_record</c> / <c>antilag_find</c> / <c>antilag_takebackorigin</c>). Each sample is a
/// <c>(Time, Origin)</c> pair recorded once per server frame; at fire time the server rewinds every
/// rewindable entity to where the shooter saw it (<see cref="SampleAt"/>) so a high-ping player's shot
/// hits the target it was aiming at on its screen.
///
/// Mirrors the QC storage exactly: a ring of <see cref="Capacity"/> (<c>ANTILAG_MAX_ORIGINS</c> = 64)
/// samples, the newest at the head, recorded monotonically in time (<c>antilag_record</c> rejects a
/// stamp that is not newer than the current head). Sampling between frames linearly interpolates the two
/// bracketing samples (the QC <c>lerpv</c> in <c>antilag_takebackorigin</c>).
///
/// Allocation discipline: one array allocated up front, samples stored by value, integer head/count —
/// no per-frame allocation on the record or sample hot paths. One buffer per rewindable entity; the
/// server owns the dictionary of these and the <c>setorigin</c> callback, so this type stays
/// engine-agnostic (see <see cref="LagCompensation"/>).
/// </summary>
public sealed class AntilagBuffer
{
    /// <summary>Ring capacity — equals the engine's <c>ANTILAG_MAX_ORIGINS</c> (server/antilag.qc). At a
    /// typical 60–72 Hz tick this is ~0.9–1.1s of history, comfortably more than the 0.4s rewind cap.</summary>
    public const int Capacity = 64;

    // Parallel ring arrays (QC: antilag_origins[]/antilag_times[]). Stored separately rather than as a
    // struct[] so the layout matches the QC and the (Time, Origin) read in the lerp is two simple loads.
    private readonly float[] _times = new float[Capacity];
    private readonly Vector3[] _origins = new Vector3[Capacity];

    // Physical index of the newest sample (QC antilag_index). -1 until the first Store.
    private int _head = -1;
    // Number of valid samples in the ring (0..Capacity). Grows to Capacity then stays pinned as the ring wraps.
    private int _count;

    /// <summary>True once at least one sample has been recorded (QC: a non-sentinel <c>antilag_times</c> entry).</summary>
    public bool HasData => _count > 0;

    /// <summary>Number of valid samples currently held (0..<see cref="Capacity"/>). Exposed for tests/diagnostics.</summary>
    public int Count => _count;

    /// <summary>
    /// Append a position sample, overwriting the oldest when the ring is full. Port of the record step in
    /// <c>antilag_record</c>: advance the head (wrapping at <see cref="Capacity"/>) and write the new
    /// <c>(time, origin)</c>. Recording is monotonic in time — a <paramref name="time"/> not strictly newer
    /// than the current head is dropped (the QC <c>if (time &lt; store.antilag_times[index]) return;</c>
    /// guard; we use <c>&lt;=</c> so a duplicate frame stamp can't create a zero-width interval the lerp
    /// would divide by).
    /// </summary>
    public void Store(float time, Vector3 origin)
    {
        // Reject stale / duplicate stamps so the ring stays strictly increasing in time (head-relative).
        if (_count > 0 && time <= _times[_head]) return;

        _head++;
        if (_head >= Capacity) _head = 0;
        _times[_head] = time;
        _origins[_head] = origin;
        if (_count < Capacity) _count++;
    }

    /// <summary>
    /// Return the entity's origin at <paramref name="time"/> by linear interpolation between the two
    /// bracketing samples — the port of <c>antilag_takebackorigin</c>'s <c>lerpv</c>. Walks newest→oldest
    /// (handling the ring wrap) to find the pair where <c>older.Time &lt;= time &lt;= newer.Time</c> and
    /// blends them.
    ///
    /// Clamping: a <paramref name="time"/> newer than the newest sample returns the newest origin (the QC
    /// "IN THE PRESENT" case, where <c>antilag_find</c> returns -1); a time older than the oldest sample
    /// returns the oldest origin. (The QC's <c>antilag_find</c> also collapses the older-than-oldest case to
    /// "present"; here we clamp to the oldest held sample instead, which is the correct edge of the recorded
    /// history.) An empty buffer returns <see cref="Vector3.Zero"/>.
    /// </summary>
    public Vector3 SampleAt(float time)
    {
        if (_count == 0) return Vector3.Zero;

        // Newest sample (head) and oldest sample (count-1 steps back from head).
        int newest = _head;
        if (time >= _times[newest]) return _origins[newest]; // newer than newest → present (clamp)

        int oldestPos = _count - 1; // logical position from the head (0 = newest)
        int oldest = PhysicalIndex(oldestPos);
        if (time <= _times[oldest]) return _origins[oldest]; // older than oldest → clamp to oldest

        // Walk newest→oldest looking for the bracket [older, newer] with older.Time <= time <= newer.Time.
        // 'hi' is the newer end of the candidate pair, 'lo' the older end (one logical step toward the tail).
        for (int k = 0; k < _count - 1; k++)
        {
            int hi = PhysicalIndex(k);
            int lo = PhysicalIndex(k + 1);
            float tHi = _times[hi];
            float tLo = _times[lo];
            if (time <= tHi && time >= tLo)
            {
                // QC lerpv: v0 + (v1 - v0) * ((t - t0) / (t1 - t0)). tHi > tLo here (monotonic ring, and the
                // exact-equal endpoints were handled by the clamps above), so the denominator is non-zero.
                float f = (time - tLo) / (tHi - tLo);
                return _origins[lo] + (_origins[hi] - _origins[lo]) * f;
            }
        }

        // Unreachable: time is strictly inside (oldest, newest) so some adjacent pair must bracket it.
        // Fall back to the newest rather than risk a bad read.
        return _origins[newest];
    }

    /// <summary>Reset the history (port of the array-clearing half of <c>antilag_clear</c>) — call on entity
    /// (re)spawn / teleport so the next fire doesn't rewind through stale positions.</summary>
    public void Clear()
    {
        _head = -1;
        _count = 0;
    }

    /// <summary>Map a logical position (0 = newest/head, increasing toward the oldest) to its physical ring
    /// index, wrapping. Kept private and branch-light; the ring walk in <see cref="SampleAt"/> is the only
    /// caller and it stays within <c>[0, _count)</c>.</summary>
    private int PhysicalIndex(int stepsBackFromHead)
    {
        int i = _head - stepsBackFromHead;
        if (i < 0) i += Capacity;
        return i;
    }
}

/// <summary>
/// Pure time/position helpers for server-side lag compensation (antilag), factored out so the rewind
/// math can live in <c>XonoticGodot.Net</c> without referencing the engine's entities or <c>setorigin</c>.
/// The structural port of how <c>server/antilag.qc</c> drives a shot:
/// <list type="number">
///   <item>Each server frame, the server calls <see cref="AntilagBuffer.Store"/> once per rewindable
///         entity (the QC <c>antilag_record</c>).</item>
///   <item>At fire time it computes the rewind time with <see cref="ComputeTakebackTime"/>
///         (QC <c>time - lag</c>, where <c>lag = ANTILAG_LATENCY</c>), then for each rewindable entity
///         calls <see cref="AntilagBuffer.SampleAt"/> and <c>setorigin</c>s it there
///         (<c>antilag_takeback</c>).</item>
///   <item>It runs the hitscan / projectile trace against those rewound positions, then restores every
///         entity to its saved present origin (<c>antilag_restore</c>).</item>
/// </list>
/// Only steps that mutate engine entities (setorigin / save / restore) live in the server layer; this
/// class provides nothing but the pure scalar/vector math so it is trivially testable and Godot-free.
/// </summary>
public static class LagCompensation
{
    /// <summary>Stock cap on how far a shot may be rewound, in seconds — the engine's
    /// <c>min(0.4, ...)</c> in <c>ANTILAG_LATENCY</c> (server/antilag.qh). Bounds the advantage a very
    /// high-ping client gets and the depth of history that must be retained.</summary>
    public const float MaxDelay = 0.4f;

    /// <summary>
    /// The server-time stamp to record a position sample at this frame — the QC <c>altime</c> from
    /// <c>server/world.qc:EndFrame</c> (<c>altime = time + frametime * (1 + autocvar_g_antilag_nudge)</c>).
    /// The +1 frametime accounts for the engine advancing <c>time</c> by a frametime AFTER the gamecode
    /// frame and then networking it (the comment block in <c>EndFrame</c>); the nudge is an extra tunable
    /// fraction of a frametime (<c>g_antilag_nudge</c>, stock 0). Recording at <c>altime</c> rather than the
    /// current <c>time</c> aligns the history with the time the client will actually see this frame, so the
    /// later takeback (<see cref="ComputeTakebackTime"/>) lands the entity where the shooter saw it.
    /// </summary>
    /// <param name="serverTime">Current authoritative server time, seconds (QC <c>time</c>).</param>
    /// <param name="frameTime">The fixed tick length, seconds (QC <c>frametime</c>).</param>
    /// <param name="nudge">The <c>g_antilag_nudge</c> tunable (stock 0); a fraction of a frametime.</param>
    public static float RecordTime(float serverTime, float frameTime, float nudge)
        => serverTime + frameTime * (1f + nudge);

    /// <summary>
    /// Compute the server time to rewind rewindable entities to for a shot fired now. Port of the
    /// <c>time - lag</c> used by <c>antilag_takeback_all</c>, where the shooter's latency is
    /// <c>ANTILAG_LATENCY(e) = min(0.4, ping)</c> — ping alone, with NO added ticrate (the "add one ticrate?"
    /// next to that macro in <c>antilag.qh</c> is a commented-out musing, never applied). The returned time is
    /// <c>serverTime - bound(0, clientPing + interpolationDelay, <see cref="MaxDelay"/>)</c>; callers mirroring
    /// stock pass <paramref name="interpolationDelay"/> = 0. The one frame-of-lag compensation QC does apply is on
    /// the RECORD side instead (<see cref="RecordTime"/>'s <c>altime = time + frametime*(1+nudge)</c>).
    /// </summary>
    /// <param name="serverTime">Current authoritative server time, seconds (QC <c>time</c>).</param>
    /// <param name="clientPing">Shooter's round-trip latency, seconds (QC <c>CS(e).ping * 0.001</c>).</param>
    /// <param name="interpolationDelay">Optional extra rewind beyond ping, seconds. Stock antilag adds none —
    /// pass 0 to rewind by ping alone (the faithful value); this knob exists only for non-stock tuning.</param>
    /// <returns>The past server time at which to sample rewindable entities.</returns>
    public static float ComputeTakebackTime(float serverTime, float clientPing, float interpolationDelay)
    {
        float lag = clientPing + interpolationDelay;
        // bound(0, lag, MaxDelay): clamp into [0, 0.4]. Negative latency (clock skew) rewinds nothing.
        if (lag < 0f) lag = 0f;
        else if (lag > MaxDelay) lag = MaxDelay;
        return serverTime - lag;
    }
}
