using System;
using VortexArena.Common.Math;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Game;
using Godot;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

internal readonly record struct DebrisTiming(float Ticrate, bool Sloppy, float Gravity)
{
    public static DebrisTiming Read(ICvarService? cvars, string ticrate, string sloppy, float defaultTicrate)
    {
        float step = cvars?.Has(ticrate) == true ? cvars.GetFloat(ticrate) : defaultTicrate;
        float loose = cvars?.Has(sloppy) == true ? cvars.GetFloat(sloppy) : 1f;
        float gravity = cvars?.Has("sv_gravity") == true ? cvars.GetFloat("sv_gravity") : 800f;
        return new DebrisTiming(float.IsFinite(step) ? step : defaultTicrate, loose != 0f,
            float.IsFinite(gravity) ? gravity : 800f);
    }
}

internal delegate TraceResult DebrisTrace(NVec3 from, NVec3 to, NVec3 halfSize);
internal readonly record struct DebrisImpact(NVec3 Origin, float Speed);

/// <summary>Committed MOVETYPE_BOUNCE state for local cosmetic debris.</summary>
internal struct DebrisMotion
{
    private const int MaxTicsPerFrame = 32;
    private const int MaxBumpsPerTic = 5;
    private const float BounceStop = 60f / 800f;

    public NVec3 Origin, Velocity, Angles, AngularVelocity;
    public bool Resting;
    private NVec3 _lastDrawOrigin;
    private double _pending;

    public DebrisMotion(NVec3 origin, NVec3 velocity, NVec3 angularVelocity)
    {
        Origin = _lastDrawOrigin = origin;
        Velocity = velocity;
        AngularVelocity = angularVelocity;
    }

    // MatchTicrate retains catch-up debt past the work cap. A nonpositive cvar consumes the
    // whole pending interval with one variable-length step, like NoMatchServer.
    public int Schedule(double delta, float ticrate, out double step, out double remainder)
    {
        if (Resting)
        {
            step = remainder = 0.0;
            return 0;
        }

        _pending += delta;
        step = ticrate > 0f ? ticrate : _pending;
        // QC evaluates floor(dt / tr) in single precision. A representable time just
        // before the boundary still belongs to the previous tic.
        int count = ticrate > 0f ? (int)MathF.Min(MaxTicsPerFrame, MathF.Floor((float)_pending / ticrate)) : 1;
        _pending = Math.Max(0.0, _pending - count * step);
        remainder = _pending;
        return count;
    }

    public bool Step(float dt, float gravity, float bounce, float floorZ, NVec3 halfSize,
        DebrisTrace? trace, bool removeOnTouch, bool removeOnNoImpact, bool removeInSolid,
        out DebrisImpact? firstImpact)
    {
        firstImpact = null;
        if (Resting)
            return true;

        float halfGravity = 0.5f * gravity * dt;
        Velocity.Z -= halfGravity;
        Angles += AngularVelocity * dt;

        // Shipped QC toss physics continues through the unused part of the tic after a bounce.
        float moveTime = dt;
        for (int bump = 0; bump < MaxBumpsPerTic && moveTime > 0f && Velocity != NVec3.Zero; bump++)
        {
            TraceResult hit = Sweep(Origin + Velocity * moveTime, floorZ, halfSize, trace);
            if (hit.StartSolid)
            {
                Stop();
                return !removeInSolid;
            }

            Origin = hit.EndPos;
            if (hit.Fraction >= 1f || hit.PlaneNormal == NVec3.Zero)
                break;
            if (removeOnTouch || (removeOnNoImpact && (hit.DpHitQ3SurfaceFlags & Q3SurfaceFlags.NoImpact) != 0))
                return false;

            firstImpact ??= new DebrisImpact(Origin, Velocity.Length());
            Velocity = Clip.ClipVelocity(Velocity, hit.PlaneNormal, 1f + bounce);
            if (hit.PlaneNormal.Z > 0.7f && NVec3.Dot(hit.PlaneNormal, Velocity) < gravity * BounceStop)
            {
                Stop();
                break;
            }

            moveTime *= 1f - hit.Fraction;
        }

        if (!Resting)
            Velocity.Z -= halfGravity;
        return true;
    }

    public Transform3D Draw(float remainder, float gravity, bool sloppy, float floorZ,
        NVec3 halfSize, DebrisTrace? trace, bool flattenOnGround, float sink, out bool startSolid)
    {
        startSolid = false;
        NVec3 pos = Origin;
        NVec3 angles = Angles;
        if (!Resting && remainder > 0f)
        {
            NVec3 v = Velocity;
            v.Z -= 0.5f * gravity * remainder;
            pos += v * remainder;
            angles += AngularVelocity * remainder;
            if (!sloppy)
            {
                TraceResult hit = Sweep(pos, floorZ, halfSize, trace);
                startSolid = hit.StartSolid;
                pos = startSolid ? _lastDrawOrigin : hit.EndPos;
            }
        }
        else if (Resting && flattenOnGround)
        {
            angles.X = angles.Z = 0f;
        }

        _lastDrawOrigin = pos;
        QMath.AngleVectors(angles, out NVec3 forward, out NVec3 right, out NVec3 up);
        var basis = new Basis(Coords.ToGodot(forward), Coords.ToGodot(up), Coords.ToGodot(right));
        return new Transform3D(basis, Coords.ToGodot(pos) - new Vector3(0f, sink, 0f));
    }

    private TraceResult Sweep(NVec3 to, float floorZ, NVec3 halfSize, DebrisTrace? trace)
    {
        if (trace is not null)
            return trace(Origin, to, halfSize);

        if (to.Z <= floorZ && to.Z < Origin.Z)
        {
            float fraction = Math.Clamp((Origin.Z - floorZ) / (Origin.Z - to.Z), 0f, 1f);
            var end = NVec3.Lerp(Origin, to, fraction);
            end.Z = floorZ;
            return new TraceResult { Fraction = fraction, EndPos = end, PlaneNormal = NVec3.UnitZ };
        }
        return TraceResult.Miss(to);
    }

    private void Stop()
    {
        Resting = true;
        Velocity = AngularVelocity = NVec3.Zero;
    }
}
