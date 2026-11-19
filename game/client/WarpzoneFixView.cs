using System;
using Godot;
using VortexArena.Common.Gameplay;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>Presentation-only warpzone correction for the rendered first-person or chase camera.</summary>
public static class WarpzoneFixView
{
    private const float NearClipMultiplier = 1.125f;
    private const float DefaultRollKillSpeed = 10f;

    /// <summary>Transform the rendered eye as soon as it passes through the entry plane.</summary>
    public static void TransformView(WarpzoneManager zones, ref NVec3 eye, ref NVec3 angles, float dt, bool dead)
    {
        if (WarpzoneView.FindCrossedZone(zones, eye) is not { } zone)
            return;

        eye = zone.Transform.TransformOrigin(eye);
        angles = zone.Transform.TransformAngles(angles);
        if (!dead)
        {
            float speed = FirstPersonView.Cvar("cl_rollkillspeed", DefaultRollKillSpeed);
            angles.Z *= speed == 0f ? 0f : MathF.Max(0f, 1f - dt * speed);
        }
    }

    /// <summary>Keep the actual near plane fixed and shift the camera if its expanded corners cross a seam.</summary>
    public static NVec3 FixNearClip(WarpzoneManager zones, Camera3D camera, NVec3 eye)
    {
        Vector2 size = camera.GetViewport().GetVisibleRect().Size;
        float depth = camera.Near * NearClipMultiplier;
        Span<NVec3> corners = stackalloc NVec3[]
        {
            Coords.ToQuake(camera.ProjectPosition(Vector2.Zero, depth)),
            Coords.ToQuake(camera.ProjectPosition(new Vector2(size.X, 0f), depth)),
            Coords.ToQuake(camera.ProjectPosition(new Vector2(0f, size.Y), depth)),
            Coords.ToQuake(camera.ProjectPosition(size, depth)),
        };
        return WarpzoneView.NearClipShift(zones, eye, corners);
    }
}
