using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Gameplay;
using NVec3 = System.Numerics.Vector3;

namespace VortexArena.Game.Client;

/// <summary>Client-side generator_draw and ons_generator_ray_draw from the original Onslaught mode.</summary>
internal sealed class OnslaughtGeneratorEffects
{
    private const string RayModel = "models/onslaught/ons_ray.md3";
    private const float RayLifetime = 0.60f;
    private static readonly StringName EffectOpacity = new("effect_opacity");
    private readonly List<(MeshInstance3D Mesh, float Start, float Scale)> _rays = new();
    private OnslaughtDestruction? _destruction;
    private int _pulse;
    private float _age;
    private float _nextPulse;

    public void Set(OnslaughtDestruction? state, float serverTime)
    {
        if (_destruction == state)
            return;
        foreach (var ray in _rays)
            ray.Mesh.QueueFree();
        _rays.Clear();
        _destruction = state;
        if (state is not { } destruction)
            return;

        _age = MathF.Max(0f, serverTime - destruction.Time);
        _pulse = (int)MathF.Min(OnslaughtDestruction.PulseCount, _age / OnslaughtDestruction.PulseInterval);
        _nextPulse = _age;
    }

    public void Advance(ClientWorld world, float dt)
    {
        if (_destruction is not { } destruction || dt <= 0f
            || (_pulse >= OnslaughtDestruction.PulseCount && _rays.Count == 0))
            return;

        _age += dt;
        for (int i = _rays.Count - 1; i >= 0; i--)
        {
            var ray = _rays[i];
            float age = _age - ray.Start;
            if (age >= RayLifetime)
            {
                ray.Mesh.QueueFree();
                _rays.RemoveAt(i);
                continue;
            }
            float steps = MathF.Floor(age / OnslaughtDestruction.PulseInterval);
            ray.Mesh.Scale = Vector3.One * (ray.Scale + 0.2f * steps);
            ray.Mesh.SetInstanceShaderParameter(EffectOpacity, MathF.Max(0f, 0.1f * MathF.Min(steps, 12f - steps)));
        }

        if (_pulse >= OnslaughtDestruction.PulseCount || _age < _nextPulse)
            return;

        NVec3 origin = destruction.Origin;
        if (OnslaughtDestruction.IsShockwave(_pulse))
        {
            world.OnSound("ONS_SHOCKWAVE", origin, attenuation: 1f);
            world.Effects.Spawn("ONS_SHOCKWAVE", origin, count: 6f);
        }
        if (GD.Randf() > 0.25f && world.Projectiles.ModelFactory is { } factory
            && SharedMeshCache.Instantiate(RayModel, () => factory(RayModel)) is { } mesh)
        {
            mesh.Position = Coords.ToGodot(origin);
            mesh.RotationDegrees = new Vector3((float)GD.RandRange(-360f, 360f), (float)GD.RandRange(-360f, 360f), (float)GD.RandRange(-360f, 360f));
            float scale = 8f + 5f * GD.Randf();
            mesh.Scale = Vector3.One * scale;
            mesh.SetInstanceShaderParameter(EffectOpacity, 0f);
            world.AddChild(mesh);
            _rays.Add((mesh, _age, scale));
        }

        for (int i = 0; i < 10; i++)
        {
            NVec3 range = new NVec3(30f * i);
            world.Effects.Spawn("ONS_GENERATOR_GIB", origin + RandomPosition(-range - new NVec3(0, 0, 20),
                range + new NVec3(0, 0, 20)));
        }
        if (GD.Randf() < 0.25f)
        {
            world.Effects.Explosion(origin);
            world.OnSound("ONS_GENERATOR_EXPLODE", origin, attenuation: 1f);
        }
        SpawnIfDefined(world, "ONS_GENERATOR_EXPLODE",
            origin + RandomPosition(new NVec3(-44, -44, -6), new NVec3(44, 44, 67)));
        if (OnslaughtDestruction.IsFinal(_pulse))
        {
            world.Effects.Explosion(origin);
            SpawnIfDefined(world, "ONS_GENERATOR_EXPLODE2", origin);
            world.OnSound("ROCKET_IMPACT", origin, attenuation: 1f);
        }

        _pulse++;
        _nextPulse = _age + OnslaughtDestruction.PulseInterval;
    }

    private static void SpawnIfDefined(ClientWorld world, string effect, NVec3 origin)
    {
        // The pinned stock catalog has no small/final named particle blocks. DarkPlaces treats those as no-ops.
        if (world.Effects.GetInfoBlocks(effect) is not null)
            world.Effects.Spawn(effect, origin);
    }

    private static NVec3 RandomPosition(NVec3 min, NVec3 max) =>
        new((float)GD.RandRange(min.X, max.X), (float)GD.RandRange(min.Y, max.Y), (float)GD.RandRange(min.Z, max.Z));
}
