using System.Globalization;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Formats.Bsp;

namespace VortexArena.Engine.Collision;

/// <summary>View data derivable from mounted map entities, without spawning gameplay entities.</summary>
public static class BspWarpzoneViews
{
    public static Dictionary<int, WarpzoneCamera.Pose> StaticCameras(
        BspData bsp, MapEntityFilter.GametypeContext gametype)
    {
        var entities = (bsp.EmbeddedEntities ?? bsp.Entities)
            .Where(entity => MapEntityFilter.ShouldKeepEntity(entity, gametype)).ToArray();
        var targets = entities.ToLookup(entity => Field(entity, "targetname"), StringComparer.Ordinal);
        var cameras = new Dictionary<int, WarpzoneCamera.Pose>();
        foreach (var entity in entities)
        {
            if (Field(entity, "classname") is not ("func_camera" or "func_warpzone_camera")
                || !TryInlineModel(Field(entity, "model"), out int model) || model >= bsp.Models.Length
                || IsMoving(entity) || Field(entity, "target") is not { Length: > 0 } name)
                continue;

            var candidates = targets[name].ToArray();
            if (candidates.Length != 1 || Field(candidates[0], "classname") is not ("target_position" or "info_notnull"))
                continue;

            Vector3 origin = Vector(candidates[0], "origin");
            Vector3 angles = Angles(candidates[0]);
            if (IsFinite(origin) && IsFinite(angles))
                cameras[model] = new WarpzoneCamera.Pose(origin, angles);
        }
        return cameras;
    }

    public static WarpzoneManager StaticZones(BspData bsp, MapEntityFilter.GametypeContext gametype,
        ISurfaceService surfaces)
    {
        var manager = new WarpzoneManager();
        var entities = bsp.Entities.Where(entity => MapEntityFilter.ShouldKeepEntity(entity, gametype)).ToArray();
        var zones = entities.Where(entity => Field(entity, "classname") == "trigger_warpzone").ToArray();
        var zonesByName = zones.ToLookup(entity => Field(entity, "targetname"), StringComparer.Ordinal);
        var zonesByTarget = zones.ToLookup(entity => Field(entity, "target"), StringComparer.Ordinal);
        var targetsByName = entities.ToLookup(entity => Field(entity, "targetname"), StringComparer.Ordinal);
        var positions = entities.Where(entity => Field(entity, "classname")
            is "trigger_warpzone_position" or "misc_warpzone_position")
            .ToLookup(entity => Field(entity, "target"), StringComparer.Ordinal);

        foreach (var entity in zones)
        {
            string name = Field(entity, "targetname");
            string target = Field(entity, "target");
            // A named zone can be the destination of a one-sided link, as on Stormkeep.
            var partners = target.Length > 0 ? zonesByName[target] : zonesByTarget[name];
            if ((name.Length == 0 && target.Length == 0) || name == target || IsMoving(entity)
                || (name.Length > 0 && zonesByName[name].Count() != 1) || partners.Count() != 1
                || IsMoving(partners.First())
                || !TryInlineModel(Field(entity, "model"), out int model) || model >= bsp.Models.Length)
                continue;

            Entity brush = ReadEntity(entity);
            var bounds = bsp.Models[model];
            brush.Mins = Vector(entity, "mins");
            brush.Maxs = Vector(entity, "maxs");
            if (brush.Mins == Vector3.Zero && brush.Maxs == Vector3.Zero)
                (brush.Mins, brush.Maxs) = (bounds.Mins, bounds.Maxs);
            float scale = Number(Field(entity, "scale"));
            if (scale == 0f)
                scale = Number(Field(entity, "modelscale"));
            if (scale != 0f && float.IsFinite(scale))
            {
                brush.Mins *= scale;
                brush.Maxs *= scale;
            }

            string explicitTarget = Field(entity, "killtarget");
            var aim = (explicitTarget.Length > 0 ? targetsByName[explicitTarget].FirstOrDefault() : null)
                ?? positions[name].FirstOrDefault();
            var (origin, angles, valid) = manager.DerivePlaneFromBrush(brush, aim is null ? null : ReadEntity(aim), surfaces);
            if (valid && IsFinite(origin) && IsFinite(angles))
                manager.Add(new Warpzone { InOrigin = origin, InAngles = angles, Trigger = brush, TargetName = name, Target = target });
        }
        manager.Link();
        return manager;
    }

    public static bool TryInlineModel(string model, out int index)
    {
        index = 0;
        return model.StartsWith('*')
            && int.TryParse(model.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out index)
            && index > 0;
    }

    private static Entity ReadEntity(IReadOnlyDictionary<string, string> entity) => new()
    {
        Model = Field(entity, "model"), Origin = Vector(entity, "origin"), Angles = Angles(entity),
    };

    private static bool IsMoving(IReadOnlyDictionary<string, string> entity) =>
        int.TryParse(Field(entity, "spawnflags"), out int flags) && (flags & 1) != 0;

    private static string Field(IReadOnlyDictionary<string, string> entity, string key) =>
        entity.TryGetValue(key, out string? field) ? field : "";

    private static Vector3 Angles(IReadOnlyDictionary<string, string> entity)
    {
        Vector3 angles = Vector(entity, "angles");
        return angles == Vector3.Zero ? new Vector3(0f, Number(Field(entity, "angle")), 0f) : angles;
    }

    private static Vector3 Vector(IReadOnlyDictionary<string, string> entity, string key)
    {
        string[] parts = Field(entity, key).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 3 ? Vector3.Zero : new Vector3(Number(parts[0]), Number(parts[1]), Number(parts[2]));
    }

    private static float Number(string value) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result) ? result : 0f;

    public static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
