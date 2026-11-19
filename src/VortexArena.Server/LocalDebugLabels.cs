using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Engine.Collision;
using VortexArena.Server.Bot;

namespace VortexArena.Server;

/// <summary>The local counterparts of the two stock debug_text_3d callers.</summary>
public sealed class LocalDebugLabels
{
    public const float Duration = 7f;
    public readonly record struct Label(Vector3 Position, string Text, double Created)
    {
        public float Alpha(double now) => System.Math.Clamp(1f - (float)(now - Created) / Duration, 0f, 1f);
    }

    private readonly List<Label> _labels = new();
    private object? _target;
    public IReadOnlyList<Label> Labels => _labels;

    public void ClearAim() => _target = null;

    public void Expire(double now)
    {
        if (_labels.Count != 0)
            _labels.RemoveAll(label => now - label.Created >= Duration);
    }

    public void AimAtEntity(Entity? entity, double now)
    {
        if (entity is null || entity.IsFreed || entity.Index == 0)
        {
            ClearAim();
            return;
        }
        if (!ChangeTarget(entity))
            return;

        string text = $"^7ent #{entity.Index}\n^8 netname: ^3{entity.NetName}\n^8 classname: ^5{entity.ClassName}"
            + $"\n^8 origin: ^2{FormatVector(entity.Origin)}";
        _labels.Add(new Label((entity.AbsMin + entity.AbsMax) * 0.5f, text, now));
    }

    public void AimAtWaypoint(Waypoint? waypoint, IEnumerable<Entity> entities, double now)
    {
        if (waypoint is null)
        {
            ClearAim();
            return;
        }
        if (!ChangeTarget(waypoint))
            return;

        string text = $"Entity {waypoint.Index}: {WaypointType(waypoint, entities)}^7"
            + $"\nflags: {(int)waypoint.Flags}\norigin: {FormatVector(waypoint.Origin)}";
        if (waypoint.IsBox)
            text += $"\nabsmin: {FormatVector(waypoint.AbsMin)}\nabsmax: {FormatVector(waypoint.AbsMax)}";
        _labels.Add(new Label(waypoint.Origin, text, now));
    }

    private bool ChangeTarget(object target)
    {
        if (ReferenceEquals(_target, target))
            return false;
        _target = target;
        return true;
    }

    private static string WaypointType(Waypoint wp, IEnumerable<Entity> entities)
    {
        if (wp.HasFlag(WaypointFlags.Item)) return "^1Item waypoint";
        if (wp.HasFlag(WaypointFlags.Crouch)) return "^5Crouch waypoint";
        if (wp.HasFlag(WaypointFlags.Jump)) return "^xf80Jump waypoint";
        if (wp.HasFlag(WaypointFlags.Support)) return "^2Support waypoint";
        if (wp.HardwiredTo is { Count: > 0 }) return "^x80fHardwired waypoint";
        if (wp.HasFlag(WaypointFlags.Ladder)) return "^3Ladder waypoint";
        if (!wp.HasFlag(WaypointFlags.Teleport)) return "^7Waypoint";
        if (!wp.IsBox) return "^3Warpzone waypoint";
        if (wp.HasFlag(WaypointFlags.CustomJp)) return "^3Custom jumppad waypoint";

        foreach (Entity entity in entities)
        {
            if (entity.IsFreed || entity.ClassName != "trigger_push")
                continue;
            if (CollisionWorld.BoxesOverlap(wp.AbsMin, wp.AbsMax, entity.AbsMin, entity.AbsMax))
                return "^3Jumppad waypoint";
        }
        return "^3Teleport waypoint";
    }

    private static string FormatVector(Vector3 v) => FormattableString.Invariant($"'{v.X:0.0} {v.Y:0.0} {v.Z:0.0}'");
}
