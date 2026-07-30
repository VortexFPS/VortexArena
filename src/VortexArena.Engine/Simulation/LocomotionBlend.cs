using System.Numerics;
using XonoticGodot.Common.Math;
using XonoticGodot.Formats.Sidecars;
using XonoticGodot.Common.Gameplay;

namespace XonoticGodot.Engine.Simulation;

/// <summary>
/// Synthesizes the per-tick <see cref="SkeletonAnim"/> that drives <see cref="PlayerSkeleton.FromFrames"/>,
/// from the single networked <c>Entity.Frame</c> + the player's movement state + the model's animation clips.
///
/// The server networks ONE frame index per entity; the upper/lower-body split needs a four-pose blend (legs +
/// torso). The CSQC client therefore reconstructs it (in Xonotic the <c>animdecide</c> + CSQCMODEL system).
/// This helper covers the core: the LEGS play a locomotion clip with smooth inter-frame interpolation
/// (<c>frame</c>/<c>frame2</c> + <c>lerpfrac</c>), and the TORSO shows its clip's current frame as the upper
/// body (<c>frame3</c> at full split weight) for the aim bones to bend by view pitch. This reproduces the
/// Xonotic look — legs run/strafe while the torso holds an aim pose — and is the seam where the full
/// per-action torso animation (attack/reload overlays, torso inter-frame lerp) can later be layered in.
///
/// Encoding note (see <see cref="PlayerSkeleton.FromFrames"/>): the split doubles the input lerpfracs, so the
/// legs' phase is halved here, and the torso's split weight is 0.5 (→ 1.0 after doubling). <c>lerpfrac4</c> is
/// kept 0 so the torso's frames never bleed into the lower body.
/// </summary>
public static class LocomotionBlend
{
    /// <summary>
    /// Sample a clip at <paramref name="time"/> seconds: the two bracketing absolute frame indices and the
    /// fractional lerp between them. Looping wraps the phase; a non-looping clip clamps at the last frame.
    /// </summary>
    public static (int frameA, int frameB, float lerp) SampleClip(in FrameGroup clip, float time)
    {
        int n = clip.FrameCount;
        if (n <= 1)
            return (clip.FirstFrame, clip.FirstFrame, 0f);

        float phase = time * clip.Fps;
        if (clip.Loop)
        {
            phase %= n;
            if (phase < 0f) phase += n;
        }
        else
        {
            if (phase < 0f) phase = 0f;
            else if (phase > n - 1) phase = n - 1;
        }

        int a = (int)System.MathF.Floor(phase);
        float lerp = phase - a;
        int b = clip.Loop ? (a + 1) % n : System.Math.Min(a + 1, n - 1);
        return (clip.FirstFrame + a, clip.FirstFrame + b, lerp);
    }

    /// <summary>
    /// Build the split <see cref="SkeletonAnim"/>: <paramref name="legs"/> animates smoothly (frame/frame2),
    /// <paramref name="torso"/>'s current frame is the upper body (frame3), to be bent by the aim bones.
    /// </summary>
    public static SkeletonAnim Split(in FrameGroup legs, float legsTime, in FrameGroup torso, float torsoTime)
    {
        (int la, int lb, float ll) = SampleClip(legs, legsTime);
        (int ta, int tb, float _) = SampleClip(torso, torsoTime);
        return new SkeletonAnim
        {
            Frame = la,                 // legs current
            Frame2 = lb,                // legs next (lower-body inter-frame lerp)
            Frame3 = ta,                // torso current (the upper-body pose)
            Frame4 = tb,                // torso next (reserved; not yet weighted into the upper split)
            Lerp = ll * 0.5f,           // doubled by the lower split → full legs phase
            Lerp3 = 0.5f,               // doubled by the upper split → upper body == frame3 (torso)
            Lerp4 = 0f,                 // keep torso out of the lower body
        };
    }

    /// <summary>
    /// [W14b LI3] Build the split <see cref="SkeletonAnim"/> for an ACTIVE upper-body ACTION (SHOOT this wave):
    /// the legs animate from <paramref name="legs"/>/<paramref name="legsTime"/> exactly as the static
    /// <see cref="Split(in FrameGroup,float,in FrameGroup,float)"/>, but the torso plays the action clip
    /// <paramref name="action"/> at <paramref name="actionPhase"/> seconds — its current frame in Frame3, its
    /// NEXT frame in Frame4, blended by the clip's inter-frame lerp. This is the half-wired 4-pose path the
    /// design (Risk #1) flags: it feeds a non-zero <c>Lerp4</c> so <see cref="PlayerSkeleton.FromFrames"/>'s
    /// UPPER branch animates frame3→frame4 — while the static path keeps <c>Lerp4 = 0</c> so a non-action torso
    /// is bit-identical to today. The legs base (Frame/Frame2) is untouched, so the lower body never sees the
    /// torso frames (FromFrames' lower branch pins its own Lerp4 to 0 — the fixbone re-anchor keeps the torso
    /// rooted on the legs so it can't tear).
    ///
    /// <para>Encoding: the upper split in <c>FromFrames</c> DOUBLES Lerp3+Lerp4 (so a 0.5/0.5 pair → 1.0 total).
    /// We therefore halve the torso blend here: <c>Lerp3 = (1−f)·0.5</c> on Frame3 (torso current),
    /// <c>Lerp4 = f·0.5</c> on Frame4 (torso next), where f is the action clip's inter-frame fraction.</para>
    /// </summary>
    public static SkeletonAnim Split(in FrameGroup legs, float legsTime, in FrameGroup action, float actionPhase, bool _actionTag)
    {
        (int la, int lb, float ll) = SampleClip(legs, legsTime);
        (int ta, int tb, float tf) = SampleClip(action, actionPhase);
        return new SkeletonAnim
        {
            Frame = la,                 // legs current (lower-body base)
            Frame2 = lb,                // legs next (lower-body inter-frame lerp)
            Frame3 = ta,                // torso/action current
            Frame4 = tb,                // torso/action next (now WEIGHTED into the upper body)
            Lerp = ll * 0.5f,           // doubled by the lower split → full legs phase
            Lerp3 = (1f - tf) * 0.5f,   // doubled by the upper split → (1−f) on the action's current frame
            Lerp4 = tf * 0.5f,          // doubled by the upper split → f on the action's next frame
        };
    }

    /// <summary>
    /// [W14b LI3] Port of the upper-body half of <c>animdecide_getupperanim</c> (animdecide.qc:109-153) for the
    /// CLIENT: given the networked action id <paramref name="action"/> (the server's expiry-resolved
    /// <c>NetEntityState.UpperAction</c>) + its start time <paramref name="start"/> at <paramref name="now"/>,
    /// decide whether an action overlay is playing and, if so, the clip's play PHASE (seconds since it began).
    /// Returns <c>active = false</c> for None/idle (the caller uses the stable static aim pose); for an active
    /// action it returns the elapsed phase, which the non-looping action clip clamps at its last frame
    /// (<see cref="SampleClip"/>) so a finished-but-not-yet-cleared SHOOT holds its end pose, never wraps.
    /// </summary>
    public static (bool active, float phase) SelectTorsoAction(byte action, float start, float now)
    {
        var (resolved, _) = AnimDecide.GetUpperAnim((AnimDecide.AnimUpperAction)action, start, now);
        if (resolved == AnimDecide.AnimUpperAction.None)
            return (false, 0f);
        return (true, now - start);
    }

    /// <summary>Coarse locomotion intent from the player's movement state (QC animdecide, simplified).</summary>
    public enum Locomotion { Idle, Walk, Run, Jump, Crouch, Dead }

    /// <summary>
    /// Pick the legs' locomotion clip from movement state (the torso uses a stable idle/aim clip independently).
    /// Mirrors the existing MD3 movement heuristic so skeletal and morph models read the same.
    /// </summary>
    public static Locomotion SelectLegs(float speed2d, bool onGround, bool ducked, bool dead)
    {
        if (dead) return Locomotion.Dead;
        if (!onGround) return Locomotion.Jump;
        if (ducked) return Locomotion.Crouch;
        if (speed2d > 220f) return Locomotion.Run;
        if (speed2d > 20f) return Locomotion.Walk;
        return Locomotion.Idle;
    }

    // ---------------------------------------------------------------------------------------------------------
    // Faithful animdecide locomotion (common/animdecide.qc)
    //
    // The networked anim_state / anim_upper_action / anim_lower_action / anim_time block (the upper-body ACTION
    // overlays — shoot/pain/draw/taunt/melee) is still NOT networked, so torso actions remain out of scope. But
    // the LOWER-body LOCOMOTION is decided CLIENT-SIDE in Base too: animdecide_setimplicitstate infers the
    // 8-direction movement state purely from the entity's velocity + angles (both networked here), and
    // animdecide_getloweranim maps that implicit state to the directional / duck-variant clip. None of that needs
    // any new networked field, so it is ported faithfully below to replace the coarse 6-state speed heuristic.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The full set of lower-body locomotion clips Base selects in <c>animdecide_getloweranim</c> — the standing
    /// 8-direction run set, the ducked 8-direction duckwalk set, plus idle/jump and their ducked variants and
    /// death. <see cref="Locomotion"/> (the coarse 6-state) stays for the MD3 path; this is the skeletal path.
    /// </summary>
    public enum DirLocomotion
    {
        Dead,
        Idle, Run, RunBackwards, StrafeLeft, StrafeRight,
        ForwardLeft, ForwardRight, BackLeft, BackRight, Jump,
        DuckIdle, DuckWalk, DuckWalkBackwards, DuckWalkStrafeLeft, DuckWalkStrafeRight,
        DuckWalkForwardLeft, DuckWalkForwardRight, DuckWalkBackLeft, DuckWalkBackRight, DuckJump,
    }

    // animdecide implicit-state direction bits (animdecide.qh ANIMIMPLICITSTATE_*).
    [System.Flags]
    private enum ImplicitDir { None = 0, Forward = 1, Backwards = 2, Left = 4, Right = 8 }

    /// <summary>
    /// Port of <c>animdecide_setimplicitstate</c> (animdecide.qc:248-278): the 8-direction movement detection.
    /// Projects velocity onto the entity's forward/right (from <c>makevectors(angles)</c>) and sets the
    /// FORWARD/BACKWARDS/LEFT/RIGHT bits using the engine's 0.5 cot threshold, but only once moving
    /// (<c>vdist(v, &gt;, 10)</c>). The INAIR bit is the caller's <paramref name="onGround"/>.
    /// </summary>
    private static ImplicitDir ImplicitDirection(Vector3 velocity, Vector3 angles)
    {
        QMath.AngleVectors(angles, out Vector3 fwd, out Vector3 right, out _);
        float vx = Vector3.Dot(velocity, fwd);
        float vy = Vector3.Dot(velocity, right);

        ImplicitDir s = ImplicitDir.None;
        // vdist(v, >, 10) with v.z forced to 0 — compare the 2D (forward/right) speed against 10.
        if (vx * vx + vy * vy > 10f * 10f)
        {
            float ax = System.MathF.Abs(vx), ay = System.MathF.Abs(vy);
            if (vx >  ay * 0.5f) s |= ImplicitDir.Forward;
            if (vx < -ay * 0.5f) s |= ImplicitDir.Backwards;
            if (vy >  ax * 0.5f) s |= ImplicitDir.Right;
            if (vy < -ax * 0.5f) s |= ImplicitDir.Left;
        }
        return s;
    }

    /// <summary>
    /// Faithful port of the lower-body locomotion pick in <c>animdecide_getloweranim</c> (animdecide.qc:155-243):
    /// dead → death; in-air → jump (ducked: duckjump); else the standing or ducked 8-direction set keyed on the
    /// implicit forward/back/left/right bits, falling back to idle (ducked: duckidle) when not moving. Decided
    /// entirely from networked velocity + angles + onground + ducked — no anim_* networking needed.
    /// </summary>
    public static DirLocomotion SelectLegsDirectional(Vector3 velocity, Vector3 angles, bool onGround, bool ducked, bool dead)
    {
        if (dead) return DirLocomotion.Dead;

        bool inAir = !onGround;
        ImplicitDir dir = inAir ? ImplicitDir.None : ImplicitDirection(velocity, angles);
        // Only the F/B/L/R bits matter for the directional switch (mask matches the QC switch arg).
        ImplicitDir fblr = dir & (ImplicitDir.Forward | ImplicitDir.Backwards | ImplicitDir.Left | ImplicitDir.Right);

        if (ducked)
        {
            if (inAir) return DirLocomotion.DuckJump;       // play the END of the jump anim
            return fblr switch
            {
                ImplicitDir.Forward => DirLocomotion.DuckWalk,
                ImplicitDir.Backwards => DirLocomotion.DuckWalkBackwards,
                ImplicitDir.Right => DirLocomotion.DuckWalkStrafeRight,
                ImplicitDir.Left => DirLocomotion.DuckWalkStrafeLeft,
                ImplicitDir.Forward | ImplicitDir.Right => DirLocomotion.DuckWalkForwardRight,
                ImplicitDir.Forward | ImplicitDir.Left => DirLocomotion.DuckWalkForwardLeft,
                ImplicitDir.Backwards | ImplicitDir.Right => DirLocomotion.DuckWalkBackRight,
                ImplicitDir.Backwards | ImplicitDir.Left => DirLocomotion.DuckWalkBackLeft,
                _ => DirLocomotion.DuckIdle,
            };
        }

        if (inAir) return DirLocomotion.Jump;               // play the END of the jump anim
        return fblr switch
        {
            ImplicitDir.Forward => DirLocomotion.Run,
            ImplicitDir.Backwards => DirLocomotion.RunBackwards,
            ImplicitDir.Right => DirLocomotion.StrafeRight,
            ImplicitDir.Left => DirLocomotion.StrafeLeft,
            ImplicitDir.Forward | ImplicitDir.Right => DirLocomotion.ForwardRight,
            ImplicitDir.Forward | ImplicitDir.Left => DirLocomotion.ForwardLeft,
            ImplicitDir.Backwards | ImplicitDir.Right => DirLocomotion.BackRight,
            ImplicitDir.Backwards | ImplicitDir.Left => DirLocomotion.BackLeft,
            _ => DirLocomotion.Idle,
        };
    }
}
