using System.Collections.Generic;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Net;

namespace VortexArena.Game.Client;

/// <summary>Presentation trace over the local BSP and the server's current solid inline entities.</summary>
internal sealed class RemoteShowNamesCollision(CollisionWorld world, ModelService models)
{
    private readonly EntityService _entities = new() { Models = models };
    private readonly Dictionary<int, Entity> _brushes = new();
    private readonly HashSet<int> _seen = new();
    private readonly List<int> _removed = new();
    private TraceService? _trace;

    public TraceService Sync(IReadOnlyDictionary<int, NetEntityState> colliders)
    {
        _seen.Clear();
        foreach (var (id, state) in colliders)
        {
            if (string.IsNullOrEmpty(state.Model) || state.Model[0] != '*'
                || !models.TryGetModel(state.Model, out ModelService.ModelDef model) || !model.IsBrushModel)
                continue;
            if (!float.IsFinite(state.Origin.LengthSquared()) || !float.IsFinite(state.Angles.LengthSquared()))
                continue;

            _seen.Add(id);
            if (!_brushes.TryGetValue(id, out Entity? brush))
            {
                brush = _entities.Spawn();
                brush.Solid = Solid.Bsp;
                _brushes.Add(id, brush);
            }

            if (brush.Model == state.Model && brush.Origin == state.Origin && brush.Angles == state.Angles)
                continue;
            if (brush.Model != state.Model)
                _entities.SetModel(brush, state.Model);

            brush.Angles = state.Angles;
            // Rotated geometry needs conservative broadphase bounds. The trace still clips the actual planes.
            Vector3 extent = new(Vector3.Max(Vector3.Abs(model.Mins), Vector3.Abs(model.Maxs)).Length());
            brush.Mins = state.Angles == Vector3.Zero ? model.Mins : -extent;
            brush.Maxs = state.Angles == Vector3.Zero ? model.Maxs : extent;
            _entities.SetOrigin(brush, state.Origin);
        }

        _removed.Clear();
        foreach (var (id, brush) in _brushes)
        {
            if (_seen.Contains(id))
                continue;
            _entities.Remove(brush);
            _removed.Add(id);
        }
        foreach (int id in _removed)
            _brushes.Remove(id);
        return _trace ??= new TraceService(world, _entities);
    }
}
