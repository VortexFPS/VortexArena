using System.Numerics;
using VortexArena.Common.Gameplay;  // WarpzoneManager, Warpzone, WarpzoneTransform, WarpzoneTrace
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>
/// Port of <c>lib/warpzone/client.qc</c> <c>WarpZone_FixView</c> / <c>WarpZone_FixNearClip</c> /
/// <c>WarpZone_View_Inside</c> — the client-side view fix-up for the frame the rendered eye STRADDLES a warpzone
/// seam. Base's engine renders the warpzone surface as a real portal (an <c>r_water</c> reflection/refraction pass)
/// and, when the camera origin pokes just through the IN plane before the body has teleported, rotates the whole
/// rendered view through the zone (so you see the exit, not the inside of the brush), kills the roll the crossing
/// would otherwise leave on the horizon, and pushes the near clip out so the seam surface itself doesn't clip into
/// the frustum. It also reports the camera as "inside" the zone so the host can hide the exterior warpzone model.
///
/// <para>This is pure PRESENTATION — it reads the live warpzone link transforms from
/// <see cref="WarpzoneTrace.AmbientManager"/> (the same manager the listen host's prediction + <see cref="PortalRenderer"/>
/// already read) and rotates a copy of the eye/view-angles the caller hands in. It never reads or writes a networked
/// field.</para>
///
/// <para><b>Listen-host only</b> — exactly like <see cref="PortalRenderer"/> / <see cref="LaserRenderer"/>: the zone
/// transforms live in <see cref="WarpzoneTrace.AmbientManager"/>, which a pure <c>--connect</c>/dedicated-server
/// client does not have. When the manager is absent (or no seam is straddled) this is a no-op and returns false, so
/// there is no regression on a remote client. Networking the warpzone transforms to remote clients is an explicit
/// out-of-scope follow-up (consistent with the registry's <c>warpzones.client.render</c> liveness:partial note).</para>
///
/// <para><b>Unverified in-engine.</b> The transform math is the same <see cref="WarpzoneTransform"/> the teleport +
/// portal render already use (unit-tested); the straddle-detection AABB span + roll-kill need an in-game eyeball.</para>
/// </summary>
public static class WarpzoneFixView
{
    /// <summary>QC <c>WarpZone_FixNearClip</c> <c>r_nearclip</c> push-out factor — the near clip is multiplied by
    /// this while straddling a seam so the warpzone surface plane doesn't poke into the frustum (cl_player.qc).</summary>
    public const float NearClipMultiplier = 1.125f;

    /// <summary>QC <c>cl_rollkillspeed</c> default — the degrees/second the residual view roll is eased toward 0 after
    /// a crossing so a roll-changing seam doesn't leave the horizon permanently tilted.</summary>
    public const float DefaultRollKillSpeed = 10f;

    /// <summary>
    /// Port of <c>WarpZone_FixView</c>: if the rendered eye has visually crossed a warpzone IN plane (the camera is
    /// just BEHIND the seam) but the body hasn't teleported yet, rotate the eye + view angles through that zone's
    /// IN→OUT transform so the rendered view shows the exit, ease the residual roll toward 0 (<c>cl_rollkillspeed</c>),
    /// and report the camera as inside the zone (<paramref name="insideZone"/>) so the host can hide the exterior
    /// warpzone model + apply the near-clip push (<see cref="NearClipMultiplier"/>). Returns true when it rotated the
    /// view. A pure presentation pass — <paramref name="zones"/> is the live <see cref="WarpzoneTrace.AmbientManager"/>
    /// (listen-host only; null on a remote/dedicated client → no-op, returns false).
    /// </summary>
    /// <param name="zones">The live warpzone manager (listen-host <see cref="WarpzoneTrace.AmbientManager"/>); null = no-op.</param>
    /// <param name="eyeQuake">The rendered eye/camera origin in Quake space — rotated through the seam in place when straddling.</param>
    /// <param name="viewAnglesQuake">The rendered view angles (deg, Quake pitch/yaw/roll) — rotated + roll-killed in place.</param>
    /// <param name="dt">Frame delta seconds, for the <c>cl_rollkillspeed</c> roll ease.</param>
    /// <param name="insideZone">True when the eye straddles a seam this frame (host hides the exterior model + pushes the near clip).</param>
    public static bool Apply(WarpzoneManager? zones, ref NVec3 eyeQuake, ref NVec3 viewAnglesQuake, float dt, out bool insideZone)
    {
        insideZone = false;
        if (zones is null)
            return false; // pure remote/dedicated client: no zone transforms (listen-host-only feature)

        // QC WarpZone_View_Inside: find the seam the eye has just crossed — the eye is BEHIND the IN plane
        // (planeDist < 0, the camera poked through the portal surface) AND still within the seam's trigger AABB span
        // (it's straddling the actual window, not behind some far-off coplanar wall). Of those, take the nearest plane.
        Warpzone? best = null;
        float bestAbsDist = float.MaxValue;
        foreach (Warpzone wz in zones.Zones)
        {
            if (!wz.Linked || !wz.Transform.Valid)
                continue;

            WarpzoneTransform t = wz.Transform;
            float planeDist = NVec3.Dot(eyeQuake - t.InOrigin, t.InForward);
            if (planeDist >= 0f)
                continue; // in front of (or on) the seam — not yet visually crossed

            if (!WithinSeamSpan(wz, eyeQuake))
                continue; // crossed the plane, but outside the actual portal window

            float abs = System.MathF.Abs(planeDist);
            if (abs < bestAbsDist)
            {
                bestAbsDist = abs;
                best = wz;
            }
        }

        if (best is null)
            return false;

        // QC WarpZone_FixView: rotate the rendered eye + view through the straddled seam so we render the exit.
        WarpzoneTransform tr = best.Transform;
        eyeQuake = tr.TransformOrigin(eyeQuake);
        viewAnglesQuake = tr.TransformAngles(viewAnglesQuake);
        insideZone = true;

        // QC cl_rollkillspeed: ease the residual view roll (Quake roll = view_angles.z) toward 0 so a roll-changing
        // crossing doesn't leave the horizon permanently tilted. 0 disables the kill (snap-free). dt scales the ease.
        float rollKillSpeed = FirstPersonView.Cvar("cl_rollkillspeed", DefaultRollKillSpeed);
        if (rollKillSpeed > 0f && dt > 0f)
        {
            float roll = viewAnglesQuake.Z;
            float step = rollKillSpeed * dt;
            if (System.MathF.Abs(roll) <= step)
                roll = 0f;
            else
                roll -= System.MathF.Sign(roll) * step;
            viewAnglesQuake.Z = roll;
        }

        return true;
    }

    /// <summary>
    /// True when <paramref name="eyeQuake"/> lies within the seam's trigger AABB span — the QC
    /// <c>WarpZone_View_Inside</c> "is the camera actually in the warpzone window" check. Uses the trigger entity's
    /// world bounds when present, else a small slab around the IN origin (a Porto/test zone with no live trigger).
    /// </summary>
    private static bool WithinSeamSpan(Warpzone wz, NVec3 eyeQuake)
    {
        if (wz.Trigger is { IsFreed: false } trig)
        {
            // The trigger's world AABB (origin + local mins/maxs). AbsMin/AbsMax are the engine-maintained world
            // bounds; fall back to origin+mins/maxs when they haven't been stamped (a freshly-spawned test edict).
            NVec3 lo = trig.AbsMin, hi = trig.AbsMax;
            if (lo == NVec3.Zero && hi == NVec3.Zero)
            {
                lo = trig.Origin + trig.Mins;
                hi = trig.Origin + trig.Maxs;
            }
            return eyeQuake.X >= lo.X && eyeQuake.X <= hi.X
                && eyeQuake.Y >= lo.Y && eyeQuake.Y <= hi.Y
                && eyeQuake.Z >= lo.Z && eyeQuake.Z <= hi.Z;
        }

        // No trigger volume (Porto/test zone): a small slab around the IN origin so a straddle near the plane still
        // registers without matching a far-away coplanar position.
        const float slab = 64f;
        NVec3 d = eyeQuake - wz.Transform.InOrigin;
        return System.MathF.Abs(d.X) <= slab
            && System.MathF.Abs(d.Y) <= slab
            && System.MathF.Abs(d.Z) <= slab;
    }
}
