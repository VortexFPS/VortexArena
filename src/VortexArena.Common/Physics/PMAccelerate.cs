using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Math;

namespace XonoticGodot.Common.Physics;

/// <summary>
/// The Xonotic player-acceleration math, ported verbatim from <c>qcsrc/common/physics/player.qc</c>
/// (PM_Accelerate, PM_AirAccelerate, CPM_PM_Aircontrol, AdjustAirAccelQW) and the helpers from the same
/// file (IsMoveInDirection, GeomLerp). These are the fidelity-critical functions that define
/// strafe-jumping / bunnyhopping / air-control feel, so they are kept as a faithful 1:1 translation:
/// every QuakeC expression maps to the same C# arithmetic, in the same order, on <see cref="float"/>.
///
/// Porting notes (from <c>Math/QMath.cs</c>):
///   * QC <c>a * b</c> on two vectors is the DOT product -> <see cref="QMath.Dot"/>.
///   * QC <c>vec2(v)</c> zeroes Z -> <see cref="Vec2"/>.
///   * QC <c>**</c> is exponentiation -> <see cref="MathF.Pow"/>.
///   * QC <c>normalize</c> returns zero for a zero vector -> <see cref="QMath.Normalize"/>.
/// All velocity reads/writes go through <c>player.Velocity</c> exactly where the QC touched
/// <c>this.velocity</c>, so the in-place mutation order is preserved.
/// </summary>
public static class PMAccelerate
{
    /// <summary>Drop the Z component (QC <c>vec2(v)</c> / <c>v - v_z*'0 0 1'</c>).</summary>
    public static Vector3 Vec2(Vector3 v) => new(v.X, v.Y, 0f);

    /// <summary>
    /// QC <c>AdjustAirAccelQW(accelqw, factor)</c>: re-stretch a QW accel fraction toward 1 by
    /// <paramref name="factor"/>, preserving sign. Used both for the high-speed modifier and for the
    /// speedlimit ramp inside <see cref="Accelerate"/>.
    /// </summary>
    public static float AdjustAirAccelQW(float accelqw, float factor)
    {
        // copysign(bound(0.000001, 1 - (1 - fabs(accelqw)) * factor, 1), accelqw)
        float mag = QMath.Bound(0.000001f, 1f - (1f - MathF.Abs(accelqw)) * factor, 1f);
        return MathF.CopySign(mag, accelqw);
    }

    /// <summary>
    /// QC <c>IsMoveInDirection(mv, ang)</c>: returns a 0..1 weight for how aligned the 2D wish-move
    /// <paramref name="mv"/> (x=forward, y=side) is with angle <paramref name="ang"/> (degrees), used to
    /// derive "strafity" and the aircontrol key mix.
    /// </summary>
    public static float IsMoveInDirection(Vector3 mv, float ang)
    {
        if (mv.X == 0f && mv.Y == 0f)
            return 0f; // avoid division by zero
        ang -= QMath.Rad2Deg * MathF.Atan2(mv.Y, mv.X);
        ang = Remainder(ang, 360f) / 45f;
        return ang > 1f ? 0f : ang < -1f ? 0f : 1f - MathF.Abs(ang);
    }

    /// <summary>
    /// QC <c>GeomLerp(a, lerp, b)</c>: geometric interpolation between <paramref name="a"/> and
    /// <paramref name="b"/> by exponent <paramref name="lerp"/>, with the special-casing the QC does for
    /// zero endpoints. Drives the strafe-speed / strafe-accel blends in the air branch.
    /// </summary>
    public static float GeomLerp(float a, float lerp, float b)
    {
        // a == 0 ? (lerp < 1 ? 0 : b) : b == 0 ? (lerp > 0 ? 0 : a) : a * (fabs(b / a) ** lerp)
        if (a == 0f) return lerp < 1f ? 0f : b;
        if (b == 0f) return lerp > 0f ? 0f : a;
        return a * MathF.Pow(MathF.Abs(b / a), lerp);
    }

    /// <summary>
    /// QC <c>remainder(x, y)</c> — IEEE remainder (result in [-y/2, y/2]). C#'s <see cref="MathF.IEEERemainder"/>
    /// matches QuakeC's <c>remainder()</c> (both delegate to libm <c>remainder</c>).
    /// </summary>
    public static float Remainder(float x, float y) => MathF.IEEERemainder(x, y);

    /// <summary>
    /// QC <c>PM_Accelerate</c>: the QuakeWorld-style accelerate used for ground, air (the common path),
    /// water and ladders. Mutates <c>player.Velocity</c> in place.
    /// </summary>
    /// <param name="gameplayfixQ2AirAccelerate">QC <c>GAMEPLAYFIX_Q2AIRACCELERATE</c> — live Xonotic runs it
    /// <b>ON</b> (autocvar inline default 1, stats.qh:396; xonotic-server.cfg:562; replicated to CSQC via
    /// MOVEFLAG_Q2AIRACCELERATE): <paramref name="wishspeed0"/> is replaced by the clamped
    /// <paramref name="wishspeed"/>, so the accel step shrinks with the strafe/duck clamps — the limiter on
    /// mid-air strafe acceleration. The air branch passes <c>mp.GameplayFixQ2AirAccelerate</c>; call sites
    /// where wishspeed == wishspeed0 (water/ladder/fly) may omit it (no-op).</param>
    public static void Accelerate(
        Entity player, float dt, Vector3 wishdir, float wishspeed, float wishspeed0,
        float accel, float accelqw, float stretchfactor, float sidefric, float speedlimit,
        bool gameplayfixQ2AirAccelerate = false)
    {
        float speedclamp = stretchfactor > 0f ? stretchfactor
            : accelqw < 0f ? 1f   // full clamping, no stretch
            : -1f;                // no clamping

        accelqw = MathF.Abs(accelqw);

        if (gameplayfixQ2AirAccelerate)
            wishspeed0 = wishspeed; // don't need to emulate this Q1 bug

        Vector3 vel = player.Velocity;
        float vel_straight = QMath.Dot(vel, wishdir);
        float vel_z = vel.Z;
        Vector3 vel_xy = Vec2(vel);
        Vector3 vel_perpend = vel_xy - vel_straight * wishdir;

        float step = accel * dt * wishspeed0;

        float vel_xy_current = vel_xy.Length();
        if (speedlimit != 0f)
            accelqw = AdjustAirAccelQW(accelqw,
                (speedlimit - QMath.Bound(wishspeed, vel_xy_current, speedlimit)) / MathF.Max(1f, speedlimit - wishspeed));

        float vel_xy_forward  = vel_xy_current + QMath.Bound(0f, wishspeed - vel_xy_current, step) * accelqw + step * (1f - accelqw);
        float vel_xy_backward = vel_xy_current - QMath.Bound(0f, wishspeed + vel_xy_current, step) * accelqw - step * (1f - accelqw);
        vel_xy_backward = MathF.Max(0f, vel_xy_backward); // can't really go negative
        vel_straight = vel_straight + QMath.Bound(0f, wishspeed - vel_straight, step) * accelqw + step * (1f - accelqw);

        if (sidefric < 0f && QMath.Dot(vel_perpend, vel_perpend) != 0f)
        {
            // negative: only apply so much sideways friction to stay below the "braking" speed
            float f = MathF.Max(0f, 1f + dt * wishspeed * sidefric);
            float themin = (vel_xy_backward * vel_xy_backward - vel_straight * vel_straight) / QMath.Dot(vel_perpend, vel_perpend);
            if (themin <= 0f)
                vel_perpend *= f;
            else
            {
                themin = MathF.Sqrt(themin);
                vel_perpend *= MathF.Max(themin, f);
            }
        }
        else
        {
            vel_perpend *= MathF.Max(0f, 1f - dt * wishspeed * sidefric);
        }

        vel_xy = vel_straight * wishdir + vel_perpend;

        if (speedclamp >= 0f)
        {
            float vel_xy_preclamp = vel_xy.Length();
            if (vel_xy_preclamp > 0f) // prevent division by zero
            {
                vel_xy_current += (vel_xy_forward - vel_xy_current) * speedclamp;
                if (vel_xy_current < vel_xy_preclamp)
                    vel_xy *= (vel_xy_current / vel_xy_preclamp);
            }
        }

        player.Velocity = vel_xy + vel_z * new Vector3(0f, 0f, 1f);
    }

    /// <summary>
    /// QC <c>PM_AirAccelerate</c>: the Warsow-bunny air accel, selected when
    /// <c>warsowbunny_turnaccel != 0</c>. Not used by the stock Xonotic set (turnaccel = 0) but ported
    /// for fidelity with the warsow/xdf/cpma presets. Mutates <c>player.Velocity</c>.
    /// </summary>
    public static void AirAccelerate(Entity player, in MovementParameters mp, float dt, Vector3 wishdir, float wishspeed)
    {
        if (wishspeed == 0f)
            return;

        Vector3 curvel = player.Velocity;
        curvel.Z = 0f;
        float curspeed = curvel.Length();

        if (wishspeed > curspeed * 1.01f)
        {
            wishspeed = MathF.Min(wishspeed, curspeed + mp.WarsowBunnyAirForwardAccel * mp.MaxSpeed * dt);
        }
        else
        {
            float f = MathF.Max(0f, (mp.WarsowBunnyTopSpeed - curspeed) / (mp.WarsowBunnyTopSpeed - mp.MaxSpeed));
            wishspeed = MathF.Max(curspeed, mp.MaxSpeed) + mp.WarsowBunnyAccel * f * mp.MaxSpeed * dt;
        }
        Vector3 wishvel = wishdir * wishspeed;
        Vector3 acceldir = wishvel - curvel;
        float addspeed = acceldir.Length();
        acceldir = QMath.Normalize(acceldir);

        float accelspeed = MathF.Min(addspeed, mp.WarsowBunnyTurnAccel * mp.MaxSpeed * dt);

        if (mp.WarsowBunnyBackToSideRatio < 1f)
        {
            Vector3 curdir = QMath.Normalize(curvel);
            float dot = QMath.Dot(acceldir, curdir);
            if (dot < 0f)
                acceldir -= (1f - mp.WarsowBunnyBackToSideRatio) * dot * curdir;
        }

        player.Velocity += accelspeed * acceldir;
    }

    /// <summary>
    /// QC <c>CPM_PM_Aircontrol</c>: the CPMA-style air control that lets you curve momentum toward the
    /// wish direction (the heart of CPM/Xonotic air movement; active when <c>sv_aircontrol != 0</c>).
    /// <paramref name="moveValues"/> is the 2D wish-move (x=forward, y=side) used for the key mix.
    /// Mutates <c>player.Velocity</c>.
    /// </summary>
    public static void Aircontrol(Entity player, in MovementParameters mp, float dt, Vector3 moveValues, Vector3 wishdir, float wishspeed)
    {
        float movity = IsMoveInDirection(moveValues, 0f);
        if ((mp.AirControlFlags & (1 << 0)) != 0) // backwards
            movity += IsMoveInDirection(moveValues, 180f);
        if ((mp.AirControlFlags & (1 << 1)) != 0) // sidewards
        {
            movity += IsMoveInDirection(moveValues, 90f);
            movity += IsMoveInDirection(moveValues, -90f);
        }

        float k = 2f * movity - 1f;
        if (k <= 0f)
            return;
        if ((mp.AirControlFlags & (1 << 2)) == 0) // crouching has an impact
            k *= QMath.Bound(0f, mp.MaxAirSpeed != 0f ? wishspeed / mp.MaxAirSpeed : 0f, 1f);

        float zspeed = player.Velocity.Z;
        Vector3 v = player.Velocity;
        v.Z = 0f;
        float xyspeed = v.Length();
        v = QMath.Normalize(v);

        float dot = QMath.Dot(v, wishdir);

        if (dot > 0f) // we can't change direction while slowing down
        {
            k *= MathF.Pow(dot, mp.AirControlPower) * dt;
            xyspeed = MathF.Max(0f, xyspeed - mp.AirControlPenalty * MathF.Sqrt(MathF.Max(0f, 1f - dot * dot)) * k);
            k *= 32f * MathF.Abs(mp.AirControl);
            v = QMath.Normalize(v * xyspeed + wishdir * k);
        }

        v *= xyspeed;
        v.Z = zspeed;
        player.Velocity = v;
    }
}
