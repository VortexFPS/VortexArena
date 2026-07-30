// Port of qcsrc/client/csqcmodel_hooks.qc — CSQCPlayer_FallbackFrame (lines 414-434) and the
// CSQCPlayer_FallbackFrame_Apply driver (435-443). Remaps an animation frame index that the model is missing
// to a frame it does have, so a player model that lacks the melee / duckwalk-variant anims doesn't draw a
// broken pose. Pure (Godot-free) so it is unit-testable; the Godot glue passes a frame-duration probe.

using System;

namespace XonoticGodot.Engine.Simulation;

/// <summary>
/// The CSQC fallback-frame remap (<c>CSQCPlayer_FallbackFrame</c>). Given a frame index and a
/// <paramref name="frameDuration"/> probe (QC <c>frameduration(modelindex, f)</c> — &gt;0 when the model has
/// that frame), it returns the frame to actually play: the original when it exists, the same when the model is
/// static (can't be fixed), else the remap-table substitute (melee→shoot, all duckwalk variants→duckwalk).
/// Only runs on non-local player models (the <c>!isplayer</c> skeleton branch at csqcmodel_hooks.qc:702).
/// </summary>
public static class CsqcFallbackFrame
{
    /// <summary>
    /// QC <c>CSQCPlayer_FallbackFrame(this, f)</c>. <paramref name="frameDuration"/> mirrors the engine
    /// builtin <c>frameduration(this.modelindex, f)</c>: it returns a positive duration when frame <c>f</c>
    /// exists in the current model, &lt;=0 otherwise.
    /// </summary>
    public static int Remap(int f, Func<int, float> frameDuration)
    {
        if (frameDuration is null) throw new ArgumentNullException(nameof(frameDuration));

        if (frameDuration(f) > 0f)
            return f; // goooooood — the model has this frame
        if (frameDuration(1) <= 0f)
            return f; // this is a static model. We can't fix it if we wanted to
        switch (f)
        {
            case 23: return 11; // anim_melee -> anim_shoot
            case 24: return 4;  // anim_duckwalkbackwards   -> anim_duckwalk
            case 25: return 4;  // anim_duckwalkstrafeleft  -> anim_duckwalk
            case 26: return 4;  // anim_duckwalkstraferight -> anim_duckwalk
            case 27: return 4;  // anim_duckwalkforwardright-> anim_duckwalk
            case 28: return 4;  // anim_duckwalkforwardleft -> anim_duckwalk
            case 29: return 4;  // anim_duckwalkbackright   -> anim_duckwalk
            case 30: return 4;  // anim_duckwalkbackleft    -> anim_duckwalk
        }
        // LOG_DEBUGF("Frame %d missing in model %s, and we have no fallback - FAIL!", f, this.model);
        return f;
    }
}
