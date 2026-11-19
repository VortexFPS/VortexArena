using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Formats.Vmap;
using VortexArena.Game.Client;
using VortexArena.Server;
using VortexArena.Server.Bot;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Net;

public sealed partial class NetGame
{
    private LocalDebugLabelLayer? _debugLabelLayer;
    private double _nextWaypointLabelAt;

    private void AttachLocalDebugLabels(CanvasLayer canvas)
    {
        if (!_isListenServer)
            return;

        _debugLabelLayer = new LocalDebugLabelLayer { Name = "LocalDebugLabels", Camera = _camera, MouseFilter = Control.MouseFilterEnum.Ignore };
        canvas.AddChild(_debugLabelLayer);
    }

    // Use the gameplay render clock for both the fade and the editor's half-second sampling interval.
    private void UpdateLocalDebugLabels(double now, bool advancing)
    {
        if (_debugLabelLayer is not { } layer || _serverWorld is not { } world)
            return;

        bool waypointTool = _editor is { Active: true, Tool: EditorTool.Waypoint };
        bool entityLabels = world.Services.Cvars.GetFloat("sv_show_entnum") != 0f;
        if (!_cameraReady || _client is not { Accepted: true, ConnectionLost: false } || (!waypointTool && !entityLabels))
        {
            layer.Labels.ClearAim();
            _nextWaypointLabelAt = 0;
        }
        else if (advancing)
        {
            object? gate = _server?.SimGate;
            if (gate is null)
                SampleLocalDebugAim(world, layer.Labels, waypointTool, now);
            else
                lock (gate)
                    SampleLocalDebugAim(world, layer.Labels, waypointTool, now);
        }
        layer.Refresh(now);
    }

    private void SampleLocalDebugAim(GameWorld world, LocalDebugLabels labels, bool waypointTool, double now)
    {
        Player? player = LocalServerPlayer;
        ITraceService trace = world.Services.TraceImpl;
        float range = WeaponFiring.CurrentMaxShotDistance;
        if (waypointTool)
        {
            if (now < _nextWaypointLabelAt)
                return;

            _nextWaypointLabelAt = now + 0.5;
            (NVec3 origin, NVec3 direction) = _editor!.ActiveRay();
            TraceResult obstruction = trace.Trace(origin, NVec3.Zero, NVec3.Zero, origin + direction * range,
                MoveFilter.Normal, player);
            WaypointNetwork? network = world.Bots.EnsureWaypointNetwork();
            Waypoint? target = network is null || obstruction.StartSolid
                ? null
                : WaypointEditor.PickAimed(network, origin, direction, range * obstruction.Fraction);
            labels.AimAtWaypoint(target, world.Services.EntityTable.All, now);
            return;
        }

        _nextWaypointLabelAt = 0;
        Transform3D camera = _camera.GlobalTransform;
        NVec3 eye = Coords.ToQuake(camera.Origin);
        NVec3 forward = Coords.ToQuake(-camera.Basis.Z);
        TraceResult hit = WarpzoneTrace.TraceWarpzone(trace, world.Warpzones, eye, NVec3.Zero, NVec3.Zero,
            eye + forward * range, MoveFilter.NormalWithVisibleTriggers, player).Trace;
        labels.AimAtEntity(hit.Ent, now);
    }
}
