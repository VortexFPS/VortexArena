// Port of Base/darkplaces/view.c V_CalcRefdefUsing and V_CalcIntermissionRefdef, as far as a client
// program that calls V_CalcRefdef (#640) needs them: the eye position from the entity (stair
// smoothing, the smoothed view height, the punch vector) and the view angles (punch angle, death
// tilt). The third-person camera (chase_active), view bobbing and the gun's follow/lean matrices are
// not ported: Xonotic's program does all three itself and leaves the engine's cvars for them at zero.
using VortexArena.Legacy.Csqc;
using VortexArena.QuakeC;

namespace VortexArena.Legacy.Presentation;

/// <summary>The engine cvars and client state V_CalcRefdefUsing reads.</summary>
public struct LegacyRefdefSettings
{
    /// <summary>cl_stairsmoothspeed (160): units per second the eye may travel to catch up after a step. 0 turns it off.</summary>
    public float StairSmoothSpeed;
    /// <summary>cl.movevars_stepheight (sv_stepheight).</summary>
    public float StepHeight;
    /// <summary>cl_smoothviewheight (0.05): seconds over which a change of view height (crouching) is blended. 0 is instant.</summary>
    public float SmoothViewHeight;
    /// <summary>v_deathtilt (1) and v_deathtiltangle (80).</summary>
    public bool DeathTilt;
    public float DeathTiltAngle;
    /// <summary>cl.punchangle and cl.punchvector, from svc_clientdata.</summary>
    public QcVector PunchAngle, PunchVector;

    public static LegacyRefdefSettings Default => new()
    {
        StairSmoothSpeed = 160, StepHeight = 18, SmoothViewHeight = 0.05f, DeathTilt = true, DeathTiltAngle = 80,
    };
}

/// <summary>
/// The engine's first-person view calculation, with the state it carries from frame to frame (where
/// the smoothed eye is, the averaged view height).
/// </summary>
public sealed class LegacyRefdef
{
    private double _stairSmoothTime, _previousTime;
    private float _stairSmoothZ, _viewHeightAverage;
    private bool _punchApplied;

    /// <summary>A new frame: the punch angle may be applied again ("don't apply punchangle twice if the scene is rendered more than once").</summary>
    public void BeginFrame() => _punchApplied = false;

    /// <summary>Forgets the smoothing state (a new level).</summary>
    public void Reset()
    {
        _stairSmoothTime = _previousTime = 0;
        _stairSmoothZ = _viewHeightAverage = 0;
        _punchApplied = false;
    }

    /// <param name="input">The entity the program named: its origin (the origin of its render matrix), view height and flags.</param>
    /// <param name="viewAngles">cl.csqc_viewangles as the program has them now.</param>
    /// <param name="time">cl.time. <paramref name="oldTime"/> is cl.oldtime, the previous frame's.</param>
    public void Calculate(in LegacyRefdefInput input, QcVector viewAngles, double time, double oldTime, in LegacyRefdefSettings settings,
        out QcVector viewOrigin, out QcVector outAngles)
    {
        _previousTime = Math.Max(_previousTime, oldTime);
        viewOrigin = input.Origin;
        outAngles = viewAngles;

        // "calculate how much time has passed since the last V_CalcRefdef"
        float smoothTime = (float)Math.Clamp(time - _stairSmoothTime, 0, 0.1);
        _stairSmoothTime = time;

        if (input.Intermission)
        {
            // V_CalcIntermissionRefdef: "entity is a fixed camera, just copy the matrix", raised by the view height.
            viewOrigin.Z += input.ViewHeight;
            outAngles = input.Angles;
            _previousTime = time;
            return;
        }

        // smooth stair stepping, but only if on the ground and enabled
        if (!input.OnGround || settings.StairSmoothSpeed <= 0 || input.Teleported)
            _stairSmoothZ = viewOrigin.Z;
        else if (_stairSmoothZ < viewOrigin.Z)
            viewOrigin.Z = _stairSmoothZ = Bound(viewOrigin.Z - settings.StepHeight, _stairSmoothZ + smoothTime * settings.StairSmoothSpeed, viewOrigin.Z);
        else if (_stairSmoothZ > viewOrigin.Z)
            viewOrigin.Z = _stairSmoothZ = Bound(viewOrigin.Z, _stairSmoothZ - smoothTime * settings.StairSmoothSpeed, viewOrigin.Z + settings.StepHeight);

        // "apply the viewofs (even if chasecam is used)", smoothed "so that things like crouching are done with a transition"
        float blend = (float)Math.Clamp((time - _previousTime) / Math.Max(0.0001, settings.SmoothViewHeight), 0, 1);
        _viewHeightAverage = _viewHeightAverage * (1 - blend) + input.ViewHeight * blend;
        viewOrigin.Z += _viewHeightAverage;

        // first person view from entity
        if (input.Dead && settings.DeathTilt) outAngles.Z = settings.DeathTiltAngle;
        viewOrigin.X += settings.PunchVector.X;
        viewOrigin.Y += settings.PunchVector.Y;
        viewOrigin.Z += settings.PunchVector.Z;
        if (!input.Dead && !_punchApplied)
        {
            outAngles.X += settings.PunchAngle.X;
            outAngles.Y += settings.PunchAngle.Y;
            outAngles.Z += settings.PunchAngle.Z;
            _punchApplied = true;
        }
        _previousTime = time;
    }

    // DarkPlaces' bound(min, value, max): min wins when the range is inverted.
    private static float Bound(float min, float value, float max) => value >= min ? (value < max ? value : max) : min;
}
