using System.Numerics;

namespace VortexArena.Common.Gameplay;

/// <summary>Authoritative generator death origin, preferred camera yaw and server time.</summary>
public readonly record struct OnslaughtDestruction(Vector3 Origin, float Yaw, float Time)
{
    public const int PulseCount = 40;
    public const float PulseInterval = 0.05f;

    // ons_camSetup checks cardinal directions first, then diagonals, keeping the first best trace.
    private static readonly int[] CameraYaws = [0, 90, 180, 270, 45, 135, 225, 315];

    public static float ChooseYaw(Func<float, float> clearFraction)
    {
        float bestFraction = 0f;
        int bestYaw = 0;
        foreach (int yaw in CameraYaws)
        {
            float fraction = clearFraction(yaw);
            if (fraction <= bestFraction)
                continue;
            bestFraction = fraction;
            bestYaw = yaw;
            if (fraction >= 1f)
                break;
        }
        return bestYaw;
    }

    public static bool IsShockwave(int pulse) => pulse is 0 or 20;

    public static bool IsFinal(int pulse) => pulse == PulseCount - 1;
}
