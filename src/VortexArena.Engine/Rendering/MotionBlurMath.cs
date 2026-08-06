using System;
using System.Numerics;

namespace VortexArena.Engine.Rendering;

/// <summary>
/// How far the screen should smear for a given camera movement — the arithmetic behind <c>r_motionblur</c>,
/// kept out of the renderer so it can be tested.
///
/// <para>It lives here because the first version of this feature was wrong in a way no compiler or screenshot
/// would catch: the smear was real but roughly a tenth of what it should have been, so the setting appeared to
/// do nothing. "Does a brisk turn produce a visible smear" is a question about numbers, and numbers can be
/// asserted; the shader that consumes the answer cannot be, in a headless test.</para>
///
/// <para><b>Framerate independence.</b> Smearing by raw per-frame motion would make the effect vanish at 300
/// fps and streak violently at 40 — the same cvar would mean something different on every machine. The offset
/// is therefore normalised to <see cref="ReferenceFrameTime"/>, so a given turn RATE produces the same smear
/// at any framerate.</para>
/// </summary>
public static class MotionBlurMath
{
    /// <summary>Frame time the strength is normalised against (~60 fps).</summary>
    public const float ReferenceFrameTime = 1f / 60f;

    /// <summary>Cap on the smear, as a fraction of screen size, so a teleport cannot streak the whole view.</summary>
    public const float MaxOffset = 0.05f;

    /// <summary>
    /// Sensitivity. The first version used 0.5, which put a brisk 300°/s turn at <c>r_motionblur 0.4</c> at
    /// about 1.7% of screen width — present, and not perceptible. DarkPlaces' 0.4 is obvious on any motion, so
    /// this should be too. See <c>Brisk_Turn_Produces_A_Visible_Smear</c> for the number this is chosen to hit.
    /// </summary>
    public const float Sensitivity = 1.5f;

    /// <summary>Distance at which sideways translation is converted to an apparent angle (world units).</summary>
    private const float TranslationReferenceDistance = 512f;

    /// <summary>Below this the pass is skipped entirely rather than blurring by nothing.</summary>
    public const float MinOffset = 0.0005f;

    /// <summary>
    /// The screen-space smear for one frame of camera movement, as a fraction of screen size.
    ///
    /// <para>Rotation is measured directly as an angle, because that is what dominates what a player
    /// perceives — a mouse flick. Sideways and vertical translation are converted to an angle at a nominal
    /// distance, so strafing smears too without needing per-pixel depth. Forward motion is deliberately
    /// ignored: it produces a zoom blur, which at these speeds reads as a rendering fault rather than motion.</para>
    /// </summary>
    /// <param name="prevForward">View forward last frame (unit).</param>
    /// <param name="nowForward">View forward this frame (unit).</param>
    /// <param name="nowRight">View right this frame (unit).</param>
    /// <param name="nowUp">View up this frame (unit).</param>
    /// <param name="move">Camera translation since last frame.</param>
    /// <param name="dt">Seconds since last frame.</param>
    /// <param name="strength">The <c>r_motionblur</c> value.</param>
    public static Vector2 Offset(Vector3 prevForward, Vector3 nowForward, Vector3 nowRight, Vector3 nowUp,
                                 Vector3 move, float dt, float strength)
    {
        if (strength <= 0f || dt <= 0f)
            return Vector2.Zero;

        float yaw = MathF.Atan2(nowForward.X, nowForward.Z) - MathF.Atan2(prevForward.X, prevForward.Z);
        yaw = Wrap(yaw);
        float pitch = MathF.Asin(Math.Clamp(nowForward.Y, -1f, 1f))
                    - MathF.Asin(Math.Clamp(prevForward.Y, -1f, 1f));

        float side = Vector3.Dot(nowRight, move) / TranslationReferenceDistance;
        float up = Vector3.Dot(nowUp, move) / TranslationReferenceDistance;

        float norm = ReferenceFrameTime / MathF.Max(dt, 1e-4f);
        var offset = new Vector2((yaw + side) * norm, (pitch + up) * norm) * strength * Sensitivity;

        float len = offset.Length();
        if (len > MaxOffset)
            offset *= MaxOffset / len;
        return offset;
    }

    /// <summary>Wrap an angle into (-π, π] so a turn across the ±π seam is not read as a full revolution.</summary>
    private static float Wrap(float a)
    {
        while (a > MathF.PI) a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }
}
