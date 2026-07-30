using VortexArena.Formats.Sidecars;

namespace VortexArena.Engine.Simulation;

/// <summary>
/// One animation channel's crossfade history — the port of Base csqcmodel's per-channel previous framegroup
/// (<c>frame3</c>/<c>frame4</c> + their ramping lerpfracs, <c>cl_model.qc:109-154</c>): when the channel's
/// clip switches, the OUTGOING clip keeps advancing on its own timeline and blends out over the fade window
/// (<c>cl_lerpanim_maxdelta_framegroups</c>). Exactly ONE previous clip is kept per channel; a switch mid-fade
/// drops the older history, like Base. Pure bookkeeping (Godot-free) so the transition rules are unit-testable;
/// <c>PlayerModel.Pose</c> owns one instance per channel (legs / torso).
/// </summary>
public sealed class AnimChannelFade
{
    /// <summary>The outgoing clip being blended out (valid while a fade is active).</summary>
    public FrameGroup PrevClip;

    /// <summary>The outgoing clip's playhead (seconds) — keeps advancing during the fade.</summary>
    public float PrevTime;

    /// <summary>Seconds since the switch; >= the window (or +infinity) = no fade active.</summary>
    public float Age = float.PositiveInfinity;

    /// <summary>True while a fade is still blending (against <paramref name="window"/>).</summary>
    public bool Active(float window) => Age < window;

    /// <summary>
    /// Capture <paramref name="outgoingClip"/> at <paramref name="playhead"/> as the fade source. Snaps
    /// instead (cancels any fade) when the window is 0 (the Base-faithful escape hatch), when this frame's
    /// <paramref name="dt"/> spiked past <paramref name="maxStep"/> (a hitch/cull gap — the "previous pose"
    /// is ancient), or when the outgoing clip is empty (nothing was showing yet).
    /// </summary>
    public void Begin(in FrameGroup outgoingClip, float playhead, float window, float dt, float maxStep)
    {
        if (window <= 0f || dt > maxStep || outgoingClip.FrameCount <= 0)
        {
            Age = float.PositiveInfinity;
            return;
        }
        PrevClip = outgoingClip;
        PrevTime = playhead;
        Age = 0f;
    }

    /// <summary>Kill any active fade (freeze/teleport-style snaps).</summary>
    public void Cancel() => Age = float.PositiveInfinity;

    /// <summary>
    /// The OLD pose's weight for this frame, then ages the fade by <paramref name="dt"/> — the outgoing clip
    /// keeps advancing on its own timeline while it blends out. Matches Base's ramp: the NEW pose weighs in at
    /// <c>age/window</c> (so the switch frame renders the old pose at weight 1, exactly like
    /// <c>l13 = (time − frame1time)/maxdelta</c> being 0 at the switch instant). Returns 0 when idle.
    /// </summary>
    public float RetainAndAdvance(float window, float dt)
    {
        if (!(Age < window)) // also catches +infinity
            return 0f;
        float retain = 1f - Age / window;
        Age += dt;
        PrevTime += dt;
        return retain;
    }
}
