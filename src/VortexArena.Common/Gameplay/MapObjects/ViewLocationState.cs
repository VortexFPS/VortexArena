using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Math;

namespace VortexArena.Common.Gameplay;

[Flags]
public enum ViewLocationFlags : byte
{
    None = 0,
    FreeAim = 1 << 1,
    FreeMove = 1 << 2,
    Track = 1 << 3,
    NoAnchorAngle = 1 << 4,
    HardSnap = 1 << 5,
    UnlockPitch = 1 << 6,
    CloseSnap = 1 << 7,
}

/// <summary>Authored camera anchors and flags for the current view-location volume.</summary>
public readonly record struct ViewLocationState(
    int TriggerId, ViewLocationFlags Flags, bool SideScroll, Vector3 Start, Vector3 End, Vector3 AnchorAngles)
{
    public static ViewLocationState? FromTrigger(Entity? trigger)
    {
        if (trigger is not { IsFreed: false, Enemy: { IsFreed: false } start, GoalEntity: { IsFreed: false } end })
            return null;

        return new(trigger.Index, (ViewLocationFlags)trigger.SpawnFlags, start != end, start.Origin, end.Origin, start.Angles);
    }

    public bool FreeAim => Flags.HasFlag(ViewLocationFlags.FreeAim);
    public bool FreeMove => Flags.HasFlag(ViewLocationFlags.FreeMove);

    public void RemapMovement(ref Vector3 move, ref Vector3 facing, bool onLadder)
    {
        if (!SideScroll)
            return;

        float forward = move.Y;
        move.Y = FreeMove && !onLadder ? move.X : 0f;
        if (facing.Y < 0f)
            move.Y = -move.Y;

        if (FreeAim)
            move.X = facing.Y > 0f ? -forward : forward;
        else
        {
            move.X = MathF.Abs(forward);
            if (move.X > 0f)
                facing.Y = QMath.VecToAngles(End - Start).Y;
        }
    }

    public Vector3 AvatarAngles(Vector3 facing, float forward, float side)
    {
        if (!SideScroll || FreeAim)
            return facing;

        Vector3 axis = Vector3.Max(Start, End) - Vector3.Min(Start, End);
        if (side != 0f)
            facing.Y = QMath.VecToAngles(side < 0f ? axis : -axis).Y;
        if (!FreeMove)
            facing.X = forward < 0f ? 50f : forward > 0f ? -50f : facing.X;
        return facing;
    }

    public Vector3 CameraOrigin(Vector3 playerOrigin) => Vector3.Clamp(playerOrigin, Vector3.Min(Start, End), Vector3.Max(Start, End));

    public (Vector3 Origin, Vector3 Angles) ResolveCamera(Vector3 playerOrigin, ref float previousYaw)
    {
        Vector3 origin = CameraOrigin(playerOrigin);
        Vector3 angles = Flags.HasFlag(ViewLocationFlags.NoAnchorAngle) ? Vector3.Zero : AnchorAngles;
        Vector3 tracking = AimAlong(playerOrigin - origin);

        if (Flags.HasFlag(ViewLocationFlags.Track) || !SideScroll)
            angles = tracking;
        if (Flags.HasFlag(ViewLocationFlags.HardSnap))
            angles = new Vector3(Snap(tracking.X), Snap(tracking.Y), Snap(tracking.Z));
        if (Flags.HasFlag(ViewLocationFlags.CloseSnap))
        {
            angles = tracking;
            if (MathF.Abs(angles.Y - previousYaw) >= 60f)
                previousYaw = Snap(angles.Y);
            else
                angles.Y = previousYaw;
        }
        if (!Flags.HasFlag(ViewLocationFlags.UnlockPitch))
            angles = new Vector3(0f, angles.Y, 0f);
        return (origin, angles);
    }

    /// <summary>Unproject a normalized cursor at a depth along the camera's forward axis.</summary>
    public static Vector3 FreeAimRayEnd(Vector3 origin, Vector3 angles, Vector2 cursor, Vector2 frustum, float depth)
    {
        QMath.AngleVectors(angles, out Vector3 forward, out Vector3 right, out Vector3 up);
        Vector2 offset = (cursor * 2f - Vector2.One) * frustum;
        return origin + depth * (forward + right * offset.X - up * offset.Y);
    }

    /// <summary>Convert a projected world hit to avatar aim in the side-scroller's Y/Z plane.</summary>
    public static Vector3 FreeAimAngles(Vector3 playerOrigin, Vector3 eyeOffset, Vector3 hit)
    {
        hit.X = playerOrigin.X;
        return QMath.AnglesTransformNormalize(AimAlong(hit - (playerOrigin + eyeOffset)));
    }

    // QC aim_vec flips the Z direction before vectoangles. A coincident target has pitch 270.
    private static Vector3 AimAlong(Vector3 direction)
        => QMath.VecToAngles(new Vector3(direction.X, direction.Y, -direction.Z));

    // QC angle_snap_f rounds upward and wraps a result above 270 degrees to zero.
    private static float Snap(float angle) => angle > 270f ? 0f : MathF.Ceiling(angle / 90f) * 90f;
}
