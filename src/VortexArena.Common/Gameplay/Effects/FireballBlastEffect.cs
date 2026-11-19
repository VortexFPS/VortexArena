using System.Numerics;

namespace VortexArena.Common.Gameplay;

/// <summary>The expanding sphere from the primary Fireball's BFG blast (modeleffects.qc).</summary>
public readonly record struct FireballBlastEffect(Vector3 Origin, float Radius)
{
    public const string ModelPath = "models/sphere/sphere.md3";
    public const float HoldSeconds = 0.05f;
    public const float FadeSeconds = 0.25f;
    public const float PeakAlpha = 0.2f;

    public static FireballBlastEffect? Create(Vector3 origin, float radius)
        => float.IsFinite(origin.X) && float.IsFinite(origin.Y) && float.IsFinite(origin.Z)
            && float.IsFinite(radius) && radius > 0f ? new(origin, radius) : null;

    /// <summary>Convert the desired final extent to the QC model scale, in signed 1/256 units.</summary>
    public float FinalScale(float modelExtent)
        => MathF.Floor(MathF.Min(Radius / modelExtent * 256f, short.MaxValue)) / 256f;

    /// <summary>Sample local age since reception. CSQC deletes the model below 0.003 alpha.</summary>
    public static bool Sample(float age, out float scaleFraction, out float alpha)
    {
        scaleFraction = System.Math.Clamp(age / (HoldSeconds + FadeSeconds), 0f, 1f);
        alpha = PeakAlpha * System.Math.Clamp(1f - (age - HoldSeconds) / FadeSeconds, 0f, 1f);
        return alpha >= 0.003f;
    }
}
