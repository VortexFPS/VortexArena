using System.Numerics;
using XonoticGodot.Common.Math;

namespace XonoticGodot.Net;

/// <summary>
/// One received authoritative snapshot of a remote entity at a server time — the pair of these
/// (previous, current) is what the client lerps between to render smoothly between the sparse server
/// updates. Corresponds to the <c>iorigin1/iorigin2</c>, <c>itime1/itime2</c> state in
/// <c>csqcmodel/interpolate.qc</c>, collapsed into a value type.
/// </summary>
public struct Snapshot
{
    /// <summary>Server time this snapshot represents (seconds).</summary>
    public float Time;

    /// <summary>Entity origin at <see cref="Time"/>.</summary>
    public Vector3 Origin;

    /// <summary>Entity Euler angles at <see cref="Time"/> (degrees). Optional — only meaningful when angles
    /// are interpolated rather than auto-derived from motion.</summary>
    public Vector3 Angles;

    /// <summary>Entity velocity at <see cref="Time"/>, if sent. When not sent it can be derived from the
    /// origin delta (see <see cref="InterpolationBuffer.AutoVelocity"/>).</summary>
    public Vector3 Velocity;

    /// <summary>[lean] Playermodel lean offset at <see cref="Time"/> (a makevectors-space angle triple;
    /// Zero = none). Blended between snapshots like <see cref="Angles"/> so the 8-bit-quantized lean
    /// eases instead of stepping.</summary>
    public Vector3 Lean;
}

/// <summary>
/// Two-snapshot interpolation buffer for a single remote entity — the structural port of
/// <c>csqcmodel/interpolate.qc</c> (<c>InterpolateOrigin_Note</c> / <c>InterpolateOrigin_Do</c>). Holds
/// the previous and current snapshot and produces an interpolated render pose at an arbitrary client
/// time between them.
///
/// Key behaviours carried over from the QC:
/// <list type="bullet">
///   <item><b>Teleport snap</b>: an explicit teleport flag, or an origin jump &gt; <see cref="TeleportDistance"/>
///         (1000u), or a velocity jump &gt; that, collapses the interval so we don't lerp across it
///         (<c>itime1 = itime2 = time</c>).</item>
///   <item><b>Stale snap</b>: a gap of ≥ <see cref="MaxInterval"/> (0.2s) between updates also collapses the
///         interval (don't lerp across a long stall).</item>
///   <item><b>Auto-velocity</b>: when enabled, velocity is derived from the origin delta over the interval
///         (<c>IFLAG_AUTOVELOCITY</c>).</item>
///   <item><b>Auto-angles</b>: when enabled, facing is derived from the direction of motion
///         (<c>IFLAG_AUTOANGLES</c>) — handled by the caller via <c>QMath.VecToAngles</c>.</item>
/// </list>
///
/// One value-type buffer per remote entity, no per-update allocation. Angle interpolation is seam-safe:
/// it blends the forward/up basis vectors and re-derives angles via <see cref="QMath.AngleVectors"/> /
/// <see cref="QMath.VecToAngles"/> (the port of the QC <c>fixedvectoangles2</c> blend), so it does not jump
/// across the 0/360° yaw seam or gimbal near ±90° pitch — see <c>BlendAngles</c>.
/// </summary>
public sealed class InterpolationBuffer
{
    /// <summary>Origin (or velocity) jump beyond this many units cancels interpolation. QC: <c>vdist(..., >, 1000)</c>.</summary>
    public const float TeleportDistance = 1000f;

    /// <summary>Gap (seconds) between updates beyond which we stop lerping. QC: <c>dt &gt;= 0.2</c>.</summary>
    public const float MaxInterval = 0.2f;

    private Snapshot _prev;   // iorigin1 / itime1
    private Snapshot _cur;    // iorigin2 / itime2
    private bool _havePrev;
    private bool _haveCur;

    // Collapsed-interval bounds (the QC itime1/itime2 after the don't-lerp logic).
    private float _lerpStart; // itime1
    private float _lerpEnd;   // itime2
    // Whether the current interval is lerp-able (false ⇒ snap to current). Explicit flag rather than the
    // QC's "itime1 && itime2 && itime1 != itime2" sentinel, so a legitimate serverPrevTime of 0 (match
    // start) is not mistaken for "no interval".
    private bool _canLerp;

    /// <summary>Derive velocity from the origin delta instead of trusting a sent value
    /// (<c>IFLAG_AUTOVELOCITY</c>). Default on — remote velocity is usually not networked
    /// (cl_player.qc notes "No need to network velocity").</summary>
    public bool AutoVelocity = true;

    /// <summary>True once at least one snapshot has been received.</summary>
    public bool HasData => _haveCur;

    /// <summary>The most recent (current) snapshot.</summary>
    public Snapshot Current => _cur;

    /// <summary>Reset all interpolation state (port of <c>InterpolateOrigin_Reset</c>) — call on entity
    /// (re)spawn so the first update doesn't lerp from stale data.</summary>
    public void Reset()
    {
        _havePrev = _haveCur = false;
        _lerpStart = _lerpEnd = 0f;
        _canLerp = false;
        _prev = default;
        _cur = default;
    }

    /// <summary>
    /// Record a freshly received snapshot, shifting current→previous and applying the don't-lerp rules.
    /// Port of <c>InterpolateOrigin_Note</c>. <paramref name="teleported"/> is the entity's teleport bit
    /// (<c>IFLAG_TELEPORTED</c> / <c>CSQCMODEL_PROPERTY_TELEPORTED</c>) for this update;
    /// <paramref name="serverPrevTime"/> is the previous server frame time (QC <c>serverprevtime</c>) used
    /// as the lerp start when interpolating normally.
    /// </summary>
    public void Note(in Snapshot snap, bool teleported, float serverPrevTime)
    {
        float dt = _haveCur ? snap.Time - _cur.Time : 0f;

        // shift current → previous
        if (_haveCur)
        {
            _prev = _cur;
            _havePrev = true;
        }
        _cur = snap;
        _haveCur = true;

        // derive velocity from origin delta when not trusting a sent value
        if (AutoVelocity && _havePrev && _cur.Time != _prev.Time)
            _cur.Velocity = (_cur.Origin - _prev.Origin) * (1f / (_cur.Time - _prev.Time));

        // --- decide whether to interpolate or snap (cl interpolate.qc tail) ---
        if (!_havePrev)
        {
            // first sample: nothing to lerp from
            _lerpStart = _lerpEnd = snap.Time;
            _canLerp = false;
            return;
        }

        float originJump = (_cur.Origin - _prev.Origin).Length();
        float velocityJump = (_cur.Velocity - _prev.Velocity).Length();

        if (teleported || originJump > TeleportDistance
            || (AutoVelocity && velocityJump > TeleportDistance) || dt >= MaxInterval)
        {
            // IFLAG_TELEPORTED, a big origin/velocity jump, or a long stall: collapse the interval (snap).
            _lerpStart = _lerpEnd = snap.Time;
            _canLerp = false;
        }
        else
        {
            // normal case: lerp prev→cur over [serverPrevTime, snap.Time].
            _lerpStart = serverPrevTime;
            _lerpEnd = snap.Time;
            _canLerp = _lerpEnd > _lerpStart;
        }
    }

    /// <summary>
    /// Produce the interpolated render pose at client time <paramref name="now"/>. Port of
    /// <c>InterpolateOrigin_Do</c>: clamps the lerp fraction to [0, 1 + <paramref name="lerpExcess"/>]
    /// (a small overshoot is allowed, matching <c>autocvar_cl_lerpexcess</c>), then blends origin/velocity
    /// linearly and angles via the seam-safe basis-vector blend (<c>BlendAngles</c>) between the previous and
    /// current snapshot. When the interval is collapsed (teleport/stall) it returns the current snapshot
    /// unblended (a hard snap).
    /// </summary>
    public Snapshot Sample(float now, float lerpExcess = 0f)
    {
        if (!_haveCur) return default;

        // collapsed interval (snap) or not enough data: return current pose as-is
        if (!_canLerp)
            return _cur;

        float span = _lerpEnd - _lerpStart;
        float f = (now - _lerpStart) / span;
        float hi = 1f + lerpExcess;
        if (f < 0f) f = 0f; else if (f > hi) f = hi;
        float f1 = 1f - f;

        Snapshot result;
        result.Time = now;
        result.Origin = f1 * _prev.Origin + f * _cur.Origin;
        result.Velocity = f1 * _prev.Velocity + f * _cur.Velocity;
        result.Angles = BlendAngles(_prev.Angles, _cur.Angles, f);
        // [lean] the lean offset is a small angle transform (≤ ~20°, never near the pitch gimbal), so the
        // same seam-safe basis blend smooths its 8-bit wire quantization between snapshots.
        result.Lean = BlendAngles(_prev.Lean, _cur.Lean, f);
        return result;
    }

    /// <summary>
    /// Seam-safe angle interpolation — the port of <c>csqcmodel/interpolate.qc</c>'s basis-vector blend
    /// (the QC interpolates the forward/up direction vectors and re-derives angles via
    /// <c>fixedvectoangles2</c>, rather than lerping Euler components which jumps across the 0/360° yaw
    /// seam and gimbals near ±90° pitch). We build the forward+up basis at each end via
    /// <see cref="QMath.AngleVectors"/>, lerp those vectors, then recover (pitch, yaw) from the blended
    /// forward via <see cref="QMath.FixedVecToAngles"/> and roll from the blended up about that forward.
    /// </summary>
    private static Vector3 BlendAngles(Vector3 a, Vector3 b, float f)
    {
        // Fast exits keep identical-angle and endpoint cases bit-exact (and skip the trig).
        if (f <= 0f || a == b) return a;
        if (f >= 1f) return b;

        float f1 = 1f - f;

        QMath.AngleVectors(a, out Vector3 fwdA, out _, out Vector3 upA);
        QMath.AngleVectors(b, out Vector3 fwdB, out _, out Vector3 upB);

        // Blend the basis directions and re-normalize (a component lerp of two unit vectors shortens them,
        // but direction is all we need — VecToAngles ignores magnitude).
        Vector3 fwd = QMath.Normalize(f1 * fwdA + f * fwdB);
        Vector3 up = f1 * upA + f * upB;

        if (fwd == Vector3.Zero)
            // Antipodal forwards (≈180° apart): the lerp passes through zero with no defined direction.
            // Fall back to the nearer endpoint rather than producing NaNs from VecToAngles.
            return f < 0.5f ? a : b;

        // pitch + yaw from the blended forward, via the makevectors-consistent QMath.FixedVecToAngles — the QC
        // fixedvectoangles2 negates pitch for exactly this reason (see QMath.VecToAngles remarks for the convention):
        // the angles we emit must reproduce this blended forward when the RENDERER re-vectors them through
        // QMath.AngleVectors, otherwise remote players' aim would be vertically mirrored. NormalizePitch then keeps
        // it a clean small value. (Yaw round-trips cleanly and needs no fixup; this is the seam-safe yaw lerp.)
        Vector3 result = QMath.FixedVecToAngles(fwd);
        result.X = NormalizePitch(result.X);

        // Recover roll: project the blended up onto the plane perpendicular to forward, and measure its
        // signed angle from the roll-free "reference up" of the (pitch,yaw) frame. Skipped when neither
        // endpoint carries roll (the common case) to stay exact and cheap. Uses the now-consistent pitch so
        // AngleVectors(result) is the correct frame.
        if (a.Z != 0f || b.Z != 0f)
        {
            QMath.AngleVectors(result, out _, out Vector3 right0, out Vector3 up0);
            // Component of the blended up along the frame's right axis vs its up axis gives the roll angle.
            float upDot = QMath.Dot(up, up0);
            float rightDot = QMath.Dot(up, right0);
            float roll = MathF.Atan2(rightDot, upDot) * QMath.Rad2Deg;
            // QC AngleVectors' right axis points to the player's right, and a positive roll banks right;
            // the sign of rightDot above already matches that convention.
            result.Z = roll;
        }
        return result;
    }

    /// <summary>Wrap a pitch (degrees) into [-180, 180] so the emitted angle is a clean small value rather
    /// than e.g. -340 (which is geometrically equal but ugly and can confuse downstream clamps).</summary>
    private static float NormalizePitch(float pitch)
    {
        pitch %= 360f;
        if (pitch > 180f) pitch -= 360f;
        else if (pitch < -180f) pitch += 360f;
        return pitch;
    }
}
