using System;
using System.Collections.Generic;
using Godot;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Net;

namespace VortexArena.Game.Client;

/// <summary>Weapon hit particles emitted from a model's current pose, with the CSQC damage think cadence.</summary>
internal sealed class AttachedDamageEffects
{

    private readonly record struct Pending(AttachedDamageWire Hit, float Expires);
    private sealed class Attachment
    {
        public required Entity Owner;
        public required Node3D Anchor;
        public required int Bone;
        public required string Effect;
        public required string Model;
        public required bool SpawnParity;
        public AttachedDamageSchedule Schedule;
    }

    private readonly List<Pending> _pending = new();
    private readonly Dictionary<int, List<Attachment>> _active = new();
    private readonly List<int> _empty = new();
    private readonly Dictionary<(string Model, int Skin), string> _species = new();
    private float _time;

    public void Queue(AttachedDamageWire hit)
    {
        if (Mode() == 0 || ClientWorld.CvarF("cl_gentle", 0f) != 0f
            || ClientWorld.CvarF("cl_gentle_damage", 0f) != 0f)
            return;

        float min = ClientWorld.CvarF("cl_damageeffect_lifetime_min", 3f);
        float max = ClientWorld.CvarF("cl_damageeffect_lifetime_max", 6f);
        float life = AttachedDamageSchedule.Lifetime(hit.Damage, ClientWorld.CvarF("cl_damageeffect_lifetime", 0.1f), min, max);
        if (life > 0f && float.IsFinite(life))
            _pending.Add(new Pending(hit, _time + life));
    }

    private static int Mode() => (int)ClientWorld.CvarF("cl_damageeffect", 1f);

    public bool HasTarget(int id)
    {
        if (_active.ContainsKey(id))
            return true;
        foreach (var item in _pending)
            if (item.Hit.TargetId == id)
                return true;
        return false;
    }

    public void Remove(int id)
    {
        _active.Remove(id);
        _pending.RemoveAll(item => item.Hit.TargetId == id);
    }

    public void Clear()
    {
        _active.Clear();
        _pending.Clear();
        _time = 0f;
    }

    public void Advance(ClientWorld world, float delta, float serverTime, int localId, int firstPersonId)
    {
        _time += delta;
        if (_pending.Count == 0 && _active.Count == 0)
            return;

        // A normal death keeps its effects until expiry. An invisible/gibbed model, a removed model or
        // a dead-to-live transition ends its existing attachments before any new hit can be admitted.
        _empty.Clear();
        foreach (var (id, list) in _active)
        {
            bool? spawnParity = world.DamageSpawnParity?.Invoke(id);
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Attachment a = list[i];
                bool dead = a.Owner.DeadState != DeadFlag.No;
                if (a.Schedule.IsFinished(_time, dead) || spawnParity != a.SpawnParity
                    || a.Owner.IsFreed || a.Owner.Alpha <= 0f
                    || a.Owner.Model != a.Model || !GodotObject.IsInstanceValid(a.Anchor)
                    || !a.Anchor.IsInsideTree())
                    list.RemoveAt(i);
            }
            if (list.Count == 0)
                _empty.Add(id);
        }
        foreach (int id in _empty)
            _active.Remove(id);

        int consumed = 0;
        for (int i = 0; i < _pending.Count; i++)
        {
            Pending item = _pending[i];
            if (_time >= item.Expires)
                continue;

            // The snapshot may arrive on a different ENet channel. The model can also be streaming,
            // especially the local body in first person. Both waits consume the original lifetime.
            if (serverTime >= item.Hit.SnapshotTime)
            {
                if (world.DamageSpawnParity?.Invoke(item.Hit.TargetId) != item.Hit.SpawnParity)
                    continue;
                if (world.TryGetDamageTarget(item.Hit.TargetId, out Entity owner, out Node3D root))
                {
                    Add(world, item, owner, root);
                    continue;
                }
            }
            _pending[consumed++] = item;
        }
        _pending.RemoveRange(consumed, _pending.Count - consumed);

        float ticrate = ClientWorld.CvarF("cl_damageeffect_ticrate", 0.1f);
        bool distribute = ClientWorld.CvarF("cl_damageeffect_distribute", 1f) != 0f;
        bool chase = ClientWorld.CvarF("chase_active", 0f) != 0f;
        foreach (var (id, list) in _active)
        {
            foreach (Attachment a in list)
            {
                if (!a.Schedule.Tick(_time, ticrate, list.Count, distribute))
                    continue;
                if (id == firstPersonId || (id == localId && !chase) || !a.Anchor.IsVisibleInTree())
                    continue;

                Vector3 position = a.Anchor.GlobalPosition;
                if (a.Anchor is Skeleton3D skeleton)
                    position = skeleton.ToGlobal(skeleton.GetBoneGlobalPose(a.Bone).Origin);
                world.Effects.SpawnCatalogPoint(a.Effect, Coords.ToQuake(position));
            }
        }
    }

    private void Add(ClientWorld world, Pending item, Entity owner, Node3D root)
    {
        AttachedDamageWire hit = item.Hit;
        int mode = Mode();
        if (mode == 0 || owner.Alpha <= 0f || string.IsNullOrEmpty(owner.Model))
            return;

        Skeleton3D? skeleton = FindSkeleton(root);
        int bone = skeleton is null ? -1 : NearestBone(skeleton, Coords.ToGodot(owner.Origin + hit.HitOffset));
        float limit = bone >= 0 ? ClientWorld.CvarF("cl_damageeffect_bones", 5f) : mode >= 2 ? 1f : 0f;
        _active.TryGetValue(hit.TargetId, out var list);
        if ((list?.Count ?? 0) >= limit)
            return;

        Weapon weapon = Weapons.ById(hit.WeaponId);
        bool playerModel = owner.ClassName is "player" or PlayerBody.ClassName;
        string species = "";
        if (playerModel && (weapon.SpawnFlags & WeaponFlags.Bleed) != 0)
        {
            var key = (Model: owner.Model, Skin: (int)owner.Skin);
            if (!_species.TryGetValue(key, out species!))
            {
                species = world.DamageModelSpecies?.Invoke(key.Model, key.Skin) ?? "";
                _species[key] = species;
            }
        }
        string? effect = AttachedDamageHit.EffectName(weapon, playerModel, species);
        if (effect is null || !world.Effects.HasCatalogEffect(effect))
            return;

        float life = item.Expires - _time;
        if (list is null)
            _active[hit.TargetId] = list = new();
        list.Add(new Attachment
        {
            Owner = owner,
            Anchor = bone >= 0 ? skeleton! : root,
            Bone = bone,
            Effect = effect,
            Model = owner.Model,
            SpawnParity = hit.SpawnParity,
            Schedule = new AttachedDamageSchedule(_time, life, owner.DeadState != DeadFlag.No),
        });
    }

    private static Skeleton3D? FindSkeleton(Node node)
    {
        if (node is Skeleton3D skeleton)
            return skeleton;
        foreach (Node child in node.GetChildren())
            if (FindSkeleton(child) is { } childSkeleton)
                return childSkeleton;
        return null;
    }

    private static int NearestBone(Skeleton3D skeleton, Vector3 hit)
    {
        int nearest = -1;
        float distance = float.PositiveInfinity;
        for (int i = 0; i < skeleton.GetBoneCount(); i++)
        {
            string name = skeleton.GetBoneName(i);
            if (name is "" or "master" or "knee_L" or "knee_R" or "leg_L" or "leg_R")
                continue;

            float squared = hit.DistanceSquaredTo(skeleton.ToGlobal(skeleton.GetBoneGlobalPose(i).Origin));
            if (squared <= distance)
            {
                distance = squared;
                nearest = i;
            }
        }
        return nearest;
    }
}
