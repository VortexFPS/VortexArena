using System;
using System.Numerics;

namespace XonoticGodot.Tests.Camera;

/// <summary>
/// An INDEPENDENT C# transcription of Base Xonotic's client-side view smoothing + prediction-error decay,
/// line-for-line from <c>Base/data/xonotic-data.pk3dir/qcsrc/lib/csqcmodel/cl_player.qc</c>
/// (<c>CSQCPlayer_ApplySmoothing</c> lines 220-242, and <c>CSQCPlayer_GetPredictionErrorO/V</c> +
/// <c>CSQCPlayer_SetPredictionError</c> lines 44-87). This is the GROUND TRUTH the apparatus checks the port's
/// faithful mode against — the same "independent reference" discipline <c>tools/movement-ref/movement_ref.c</c>
/// uses for the physics math, extended to the camera/view layer.
///
/// It is deliberately NOT the port's <see cref="XonoticGodot.Net.Reconciler"/> (which is the code under test and
/// reimplements stair smoothing as a render-only Z offset with port-only knobs). This class reproduces exactly
/// what stock Xonotic renders as the eye Z each frame so a divergence is a genuine faithfulness bug.
///
/// <para>QC <c>bound(min, a, max)</c> == clamp(a) into [min, max]; reproduced by <see cref="Bound"/>. All math is
/// single-precision (the QC float ABI), so call sites use <see cref="float"/> throughout.</para>
/// </summary>
public sealed class CameraReferenceQc
{
    // --- stock Base cvar values (xonotic-client.cfg) ---
    /// <summary>cl_stairsmoothspeed (Base default 200 u/s). &lt;= 0 disables stair smoothing (snap).</summary>
    public float StairSmoothSpeed = 200f;
    /// <summary>cl_smoothviewheight (Base default 0.05 s). &lt;= 0 snaps the eye height (viewheight=1).</summary>
    public float SmoothViewHeight = 0.05f;
    /// <summary>cl_movement_errorcompensation (Base default 0 = OFF). Only nonzero arms the error decay.</summary>
    public float ErrorCompensation = 0f;
    /// <summary>sys_ticrate (the tick PERIOD in seconds, Base 1/72). QC factor = errorcompensation / ticrate.</summary>
    public float TicRate = 1f / 72f;
    /// <summary>PHYS_STEPHEIGHT (sv_stepheight, Xonotic 31): the max the smoothed Z may lag the live Z.</summary>
    public float StepHeight = 31f;

    // --- CSQCPlayer_ApplySmoothing persistent state (cl_player.qc globals) ---
    private float _stairsmoothz;
    private float _smoothPrevTime;
    private float _viewheightavg;
    private bool _seeded;

    // --- CSQCPlayer_SetPredictionError persistent state ---
    private Vector3 _errorO;
    private Vector3 _errorV;
    private float _errorTime;
    private float _errorFactor;

    /// <summary>Reset all smoothing state (a teleport / respawn — the engine sets <c>csqcmodel_teleported</c>).</summary>
    public void Reset()
    {
        _stairsmoothz = 0f; _smoothPrevTime = 0f; _viewheightavg = 0f; _seeded = false;
        _errorO = _errorV = Vector3.Zero; _errorTime = 0f; _errorFactor = 0f;
    }

    /// <summary>QC <c>CSQCPlayer_GetPredictionErrorO</c> — the decaying origin offset added to the rendered origin.</summary>
    public Vector3 GetPredictionErrorO(float time)
        => time >= _errorTime ? Vector3.Zero : _errorO * ((_errorTime - time) * _errorFactor);

    /// <summary>QC <c>CSQCPlayer_GetPredictionErrorV</c>.</summary>
    public Vector3 GetPredictionErrorV(float time)
        => time >= _errorTime ? Vector3.Zero : _errorV * ((_errorTime - time) * _errorFactor);

    /// <summary>QC <c>CSQCPlayer_SetPredictionError</c> (cl_player.qc:56-87): a too-big jump (origin &gt; 32 OR
    /// velocity &gt; 192) is DISCARDED; with errorcompensation off the factor is zeroed (no smoothing); otherwise the
    /// error accumulates onto the residual and re-arms a one-tick linear decay.</summary>
    public void SetPredictionError(Vector3 o, Vector3 v, float time)
    {
        // too big to compensate (teleport/jumppad/jump-time disagreement) — ignore.
        if (o.Length() > 32f || v.Length() > 192f)
            return;

        if (ErrorCompensation == 0f)
        {
            _errorFactor = 0f;
            return;
        }

        _errorO = GetPredictionErrorO(time) + o;
        _errorV = GetPredictionErrorV(time) + v;
        _errorFactor = ErrorCompensation / (TicRate != 0f ? TicRate : 1f);
        _errorTime = time + 1f / _errorFactor;
    }

    /// <summary>
    /// QC <c>CSQCPlayer_ApplySmoothing</c> (cl_player.qc:220-242): given the live (predicted) origin and the
    /// current eye state, return the rendered eye position. <paramref name="time"/> is the frame's render time,
    /// <paramref name="drawtime"/> the PREVIOUS frame's render time (QC <c>drawtime</c>), <paramref name="onground"/>
    /// the predicted PMF_ONGROUND, <paramref name="viewOfsZ"/> the live view offset (35 standing / 20 crouched),
    /// and <paramref name="teleported"/> the csqcmodel_teleported flag (snap, don't smooth).
    /// </summary>
    public Vector3 ApplySmoothing(Vector3 origin, float time, float drawtime, bool onground, float viewOfsZ,
        bool teleported = false, bool groundNetworkEntity = false)
    {
        float vz = origin.Z;

        float smoothtime = Bound(0f, time - _smoothPrevTime, 0.1f);
        _smoothPrevTime = MathF.Max(_smoothPrevTime, drawtime); // drawtime is the previous frame's time here

        if (!_seeded || teleported || !onground || StairSmoothSpeed <= 0f || groundNetworkEntity)
        {
            _stairsmoothz = vz;
            _seeded = true;
        }
        else if (_stairsmoothz < vz)
        {
            vz = _stairsmoothz = Bound(vz - StepHeight, _stairsmoothz + smoothtime * StairSmoothSpeed, vz);
        }
        else if (_stairsmoothz > vz)
        {
            vz = _stairsmoothz = Bound(vz, _stairsmoothz - smoothtime * StairSmoothSpeed, vz + StepHeight);
        }

        float viewheight = SmoothViewHeight <= 0f ? 1f : Bound(0f, (time - _smoothPrevTime) / SmoothViewHeight, 1f);
        _viewheightavg = _viewheightavg * (1f - viewheight) + viewOfsZ * viewheight;
        vz += _viewheightavg;

        _smoothPrevTime = time;
        return new Vector3(origin.X, origin.Y, vz);
    }

    /// <summary>QC <c>bound(min, a, max)</c> = clamp a into [min, max] (assumes min &lt;= max, as all call sites do).</summary>
    private static float Bound(float min, float a, float max) => a < min ? min : (a > max ? max : a);
}
