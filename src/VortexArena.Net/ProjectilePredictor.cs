using System.Numerics;

namespace XonoticGodot.Net;

/// <summary>Result of a world sweep for client-side projectile collision: did the segment hit world geometry,
/// and if so where + the surface normal. The Godot renderer fills this from <c>Api.Trace</c> (world-only,
/// matching CSQC's <c>move_nomonsters = MOVE_WORLDONLY</c>); tests supply a synthetic plane.</summary>
public readonly record struct ProjectileTraceHit(bool Hit, Vector3 Point, Vector3 Normal);

/// <summary>Sweep a projectile point from <paramref name="start"/> to <paramref name="end"/> against world
/// geometry. Injected into <see cref="ProjectilePredictor.Step"/> so the predictor stays pure/testable while
/// the collision world lives behind the engine's trace service.</summary>
public delegate ProjectileTraceHit ProjectileWorldTrace(Vector3 start, Vector3 end);

/// <summary>
/// Per-projectile client-side motion predictor — the port of CSQC's client-animated projectile path
/// (Base/.../qcsrc/client/weapons/projectile.qc <c>Projectile_Draw</c> with <c>count &amp; 0x80</c>, backed by
/// <c>Movetype_Physics_NoMatchServer</c>: a <c>MOVETYPE_FLY</c> projectile that detonates on impact is
/// ticrate-independent, so the client may advance it by raw frametime).
///
/// The reference client does NOT two-snapshot-interpolate a client-animated projectile. Instead it SNAPS the
/// entity to each authoritative server origin as it arrives and EXTRAPOLATES locally by the networked velocity
/// every render frame in between — on receive <c>this.origin = ReadVector; this.velocity = ReadVector;
/// move_time = max(move_time, time)</c>, then <c>Movetype_Physics_NoMatchServer</c> advances by
/// <c>time - move_time</c>. That is what gives a fired bolt instant, full-speed, perfectly smooth motion with
/// no interpolation delay and no ease-in.
///
/// The port previously routed projectiles through the two-snapshot <see cref="InterpolationBuffer"/> (a
/// deliberate one-snapshot lag) and then the renderer eased the node toward that already-stale origin with an
/// exponential <c>Lerp(target, dt·30)</c>. The bolt therefore accelerated from rest at spawn, trailed its true
/// position by a constant lag, and died short of the real impact — reading as "slower" than the reference even
/// though the simulated speed was identical. This predictor restores the CSQC behaviour.
///
/// Pure (System.Numerics, Quake units) so it is unit-testable away from the Godot renderer that owns the
/// visual. Straight-line extrapolation (no client gravity/collision) is exact for FLY projectiles and accurate
/// to within the second-order gravity term between snapshots (≤ ~1u at normal snapshot rates) for ballistic
/// ones — the per-snapshot velocity refresh captures the trajectory's first order — so it needs no movetype or
/// gravity networking. Client-side world tracing (stop/bounce a predicted bolt at a wall before the server's
/// removal arrives) is a deliberate follow-up; until then a fast bolt may visibly overrun a surface by up to
/// one round-trip of travel before its explosion lands at the authoritative point (negligible on a listen
/// server, where the round trip is ~0).
/// </summary>
public struct ProjectilePredictor
{
    /// <summary>The current predicted render position (Quake units).</summary>
    public Vector3 Position;

    /// <summary>The current predicted velocity (Quake units/sec) — the last authoritative value, held constant
    /// between snapshots for the straight-line extrapolation (CSQC keeps <c>this.velocity</c> until the next
    /// update).</summary>
    public Vector3 Velocity;

    private Vector3 _lastNetOrigin; // the authoritative origin we last snapped to — detects a fresh snapshot
    private bool _init;

    /// <summary>True once seeded (so a never-stepped predictor seeds itself from the first <see cref="Step"/>).</summary>
    public readonly bool Initialized => _init;

    /// <summary>(Re)initialise to a freshly spawned projectile's authoritative origin + velocity.</summary>
    public void Spawn(Vector3 origin, Vector3 velocity)
    {
        Position = origin;
        Velocity = velocity;
        _lastNetOrigin = origin;
        _init = true;
    }

    /// <summary>
    /// Advance one render frame and return the predicted position (Quake units).
    ///
    /// <paramref name="netOrigin"/>/<paramref name="netVelocity"/> are the latest authoritative values from the
    /// entity stream; <paramref name="netOrigin"/> only changes on the frame a new snapshot is applied. On such
    /// a frame we snap to the server truth and refresh the extrapolation velocity (CSQC
    /// <c>this.origin = ReadVector; this.velocity = ReadVector</c>); the motion for that step is already baked
    /// into <paramref name="netOrigin"/>, so we do NOT also extrapolate — which is what keeps a per-frame mover
    /// that updates the origin every frame (the demo driver) from double-integrating. On a frame with no new
    /// snapshot we extrapolate locally by the held velocity — the smooth, full-speed, lag-free motion between
    /// sparse server updates (CSQC <c>Movetype_Physics_NoMatchServer</c> over <paramref name="dt"/>).
    ///
    /// A discontinuity (teleport / id reuse / long stall) needs no special case: snapping to whatever the
    /// server reports IS the correct response, and the next idle frame resumes extrapolating from there.
    ///
    /// <para><paramref name="trace"/> (optional) sweeps the extrapolation segment against world geometry so a
    /// predicted bolt stops at a wall instead of overrunning it by up to a round-trip before the server's
    /// removal arrives (CSQC <c>Movetype_Physics</c> with <c>move_nomonsters = MOVE_WORLDONLY</c>). On a hit:
    /// when <paramref name="bounce"/> is false (a detonate-on-impact flier — rocket/blaster/…) the projectile
    /// halts at the surface and freezes (the server's detonation removes it a moment later); when true (a
    /// bouncing projectile — grenade/electro/…) the velocity is reflected off the plane by
    /// <c>1 + <paramref name="bounceFactor"/></c> (QC <c>_Movetype_ClipVelocity</c>) and it keeps moving. The
    /// authoritative snapshot still corrects any drift on its next arrival. A snapshot-update frame never
    /// traces — snapping to server truth is unconditional.</para>
    /// </summary>
    public Vector3 Step(Vector3 netOrigin, Vector3 netVelocity, float dt,
        ProjectileWorldTrace? trace = null, bool bounce = false, float bounceFactor = 0.5f)
    {
        if (!_init)
        {
            Spawn(netOrigin, netVelocity);
            return Position;
        }

        if (netOrigin != _lastNetOrigin)
        {
            // Fresh authoritative snapshot: snap to server truth and refresh the extrapolation velocity.
            Position = netOrigin;
            Velocity = netVelocity;
            _lastNetOrigin = netOrigin;
            return Position;
        }

        // No new data this frame: extrapolate locally by the held velocity.
        Vector3 next = Position + Velocity * dt;

        if (trace is not null && next != Position)
        {
            ProjectileTraceHit h = trace(Position, next);
            if (h.Hit)
            {
                Position = h.Point;
                if (bounce)
                    // QC _Movetype_ClipVelocity: v -= (1+bounce)·(v·n)·n — reflect and keep flying.
                    Velocity -= (1f + bounceFactor) * Vector3.Dot(Velocity, h.Normal) * h.Normal;
                else
                    // Detonate-on-impact flier: freeze at the surface until the server's removal lands.
                    Velocity = Vector3.Zero;
                return Position;
            }
        }

        Position = next;
        return Position;
    }
}
