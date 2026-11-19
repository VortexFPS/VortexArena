using System;
using System.Numerics;

namespace VortexArena.Common.Gameplay;

/// <summary>View-only queries for a rendered eye and its near-plane corners.</summary>
public static class WarpzoneView
{
    // The BSP trigger bounds approximate WarpZone_Find's brush query on the native client.
    private static bool Overlaps(Warpzone zone, Vector3 min, Vector3 max)
    {
        if (!zone.Linked || !zone.Transform.Valid || zone.Trigger is not { IsFreed: false } brush)
            return false;
        Vector3 lo = brush.Origin + brush.Mins;
        Vector3 hi = brush.Origin + brush.Maxs;
        return lo.X <= max.X && hi.X >= min.X
            && lo.Y <= max.Y && hi.Y >= min.Y
            && lo.Z <= max.Z && hi.Z >= min.Z;
    }

    public static Warpzone? FindCrossedZone(WarpzoneManager zones, Vector3 eye)
    {
        Warpzone? nearest = null;
        float nearestDist = float.NegativeInfinity;
        foreach (Warpzone zone in zones.Zones)
        {
            if (!Overlaps(zone, eye, eye))
                continue;
            WarpzoneTransform transform = zone.Transform;
            float dist = Vector3.Dot(eye - transform.InOrigin, transform.InForward);
            if (dist < 0f && dist > nearestDist)
            {
                nearest = zone;
                nearestDist = dist;
            }
        }
        return nearest;
    }

    /// <summary>Shift the eye along the entry normal by the deepest near-plane corner penetration.</summary>
    public static Vector3 NearClipShift(WarpzoneManager zones, Vector3 eye, ReadOnlySpan<Vector3> corners)
    {
        Vector3 min = eye, max = eye;
        foreach (Vector3 corner in corners)
        {
            min = Vector3.Min(min, corner);
            max = Vector3.Max(max, corner);
        }

        Vector3 shift = Vector3.Zero;
        float minDist = 0f;
        foreach (Warpzone zone in zones.Zones)
        {
            if (!Overlaps(zone, min, max))
                continue;
            WarpzoneTransform transform = zone.Transform;
            Vector3 normal = transform.InForward;
            if (Vector3.Dot(eye - transform.InOrigin, normal) < 0f)
                continue;

            foreach (Vector3 corner in corners)
            {
                float dist = Vector3.Dot(corner - transform.InOrigin, normal);
                if (dist < minDist)
                {
                    minDist = dist;
                    shift = -dist * normal;
                }
            }
        }
        return shift;
    }
}
