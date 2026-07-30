namespace XonoticGodot.Net;

/// <summary>
/// The FAITHFUL Base-Xonotic client view smoothing — a transcription of <c>CSQCPlayer_ApplySmoothing</c>
/// (Base <c>qcsrc/lib/csqcmodel/cl_player.qc</c> lines 220-242): the <c>bound()</c>-based <c>stairsmoothz</c>
/// stair glide plus the <c>viewheightavg</c> eye-height blend. Used by the net camera when
/// <c>cl_movement_smoothing_faithful</c> is on (the default), so the rendered eye matches stock Xonotic exactly
/// and the ONLY intentional divergence from Base is the stepheight processing.
///
/// <para>This is the runtime sibling of the test reference <c>CameraReferenceQc</c>; both implement the same
/// algorithm so the apparatus can prove parity. It is driven once per render frame by the real frame delta
/// (QC <c>smoothtime = bound(0, time - prevtime, 0.1)</c>; for a once-per-frame call that delta IS the frame dt,
/// so we take it directly — the same simplification the port's stair offset already uses). The split return
/// (<c>StairZ</c>, <c>ViewHeightZ</c>) maps cleanly onto the net camera's existing composition: the smoothed
/// origin Z feeds <c>ViewState.OriginQuake.Z</c> and the blended view height feeds <c>ViewState.EyeHeightZ</c>.</para>
/// </summary>
public sealed class FaithfulViewSmoothing
{
    /// <summary>cl_stairsmoothspeed (Base 200 u/s). &lt;= 0 disables the stair glide (the eye follows Z exactly).</summary>
    public float StairSmoothSpeed = 200f;
    /// <summary>cl_smoothviewheight (Base 0.05 s). &lt;= 0 snaps the eye height (no crouch blend).</summary>
    public float SmoothViewHeight = 0.05f;
    /// <summary>PHYS_STEPHEIGHT (sv_stepheight, Xonotic 31): the max the smoothed Z may lag the live Z.</summary>
    public float StepHeight = 31f;

    private float _stairsmoothz;
    private float _viewheightavg;
    private bool _seeded;

    /// <summary>Reset the smoother (a teleport / respawn — QC <c>csqcmodel_teleported</c> snaps, never smooths).</summary>
    public void Reset() { _stairsmoothz = 0f; _viewheightavg = 0f; _seeded = false; }

    /// <summary>The result of one smoothing step: the smoothed origin Z (the stair glide) and the blended eye height.</summary>
    public readonly record struct Result(float StairZ, float ViewHeightZ);

    /// <summary>
    /// Advance the smoother one render frame and return the smoothed origin Z + blended eye height.
    /// <paramref name="trueZ"/> is the live predicted origin Z, <paramref name="frameDt"/> the real frame delta
    /// (clamped to [0, 0.1] like QC's smoothtime), <paramref name="onground"/> the predicted PMF_ONGROUND,
    /// <paramref name="viewOfsZ"/> the live view offset (35 standing / 20 crouched), and <paramref name="teleported"/>
    /// /<paramref name="groundNetworkEntity"/> the snap conditions (a teleport or standing on a networked mover
    /// makes the glide snap to the live Z, exactly as QC does).
    /// </summary>
    public Result Apply(float trueZ, float frameDt, bool onground, float viewOfsZ,
        bool teleported = false, bool groundNetworkEntity = false)
    {
        float smoothtime = frameDt < 0f ? 0f : (frameDt > 0.1f ? 0.1f : frameDt);

        if (!_seeded || teleported || !onground || StairSmoothSpeed <= 0f || groundNetworkEntity)
        {
            _stairsmoothz = trueZ;
            _seeded = true;
        }
        else if (_stairsmoothz < trueZ)
        {
            _stairsmoothz = Bound(trueZ - StepHeight, _stairsmoothz + smoothtime * StairSmoothSpeed, trueZ);
        }
        else if (_stairsmoothz > trueZ)
        {
            _stairsmoothz = Bound(trueZ, _stairsmoothz - smoothtime * StairSmoothSpeed, trueZ + StepHeight);
        }

        float viewheight = SmoothViewHeight <= 0f ? 1f : Bound(0f, frameDt / SmoothViewHeight, 1f);
        _viewheightavg = _viewheightavg * (1f - viewheight) + viewOfsZ * viewheight;

        return new Result(_stairsmoothz, _viewheightavg);
    }

    /// <summary>QC <c>bound(min, a, max)</c> = clamp a into [min, max] (all call sites keep min &lt;= max).</summary>
    private static float Bound(float min, float a, float max) => a < min ? min : (a > max ? max : a);
}
