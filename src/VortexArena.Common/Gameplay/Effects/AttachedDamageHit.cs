using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay.Damage;

namespace VortexArena.Common.Gameplay;

/// <summary>A cosmetic weapon hit against a model. The position is relative to the target at trace time.</summary>
public readonly record struct AttachedDamageHit(Entity Target, int WeaponId, Vector3 HitOffset, byte Damage)
{
    public static AttachedDamageHit? Create(Entity target, string deathType, Vector3 hitLocation, float damage)
    {
        if (!(damage > 0f) || !float.IsFinite(damage)
            || target.IsFreed || target.Alpha < 0f
            || (target.ModelIndex == 0 && string.IsNullOrEmpty(target.Model))
            || DeathTypes.HasHitType(deathType, DeathTypes.Spam)
            || DeathTypes.HasHitType(deathType, DeathTypes.Sound))
            return null;

        string weaponName = DeathTypes.WeaponNetNameOf(deathType);
        if (weaponName.Length == 0 || Weapons.ByName(weaponName) is not { } weapon)
            return null;

        return new(target, weapon.RegistryId, hitLocation - target.Origin, (byte)System.Math.Clamp(damage, 1f, 255f));
    }

    public static string? EffectName(Weapon weapon, bool playerModel, string species)
    {
        string name = weapon.NetName switch
        {
            "okhmg" or "okmachinegun" => "machinegun",
            "oknex" => "vortex",
            "okshotgun" => "shotgun",
            "okrpc" => "devastator",
            _ => weapon.NetName,
        };
        if ((weapon.SpawnFlags & WeaponFlags.Bleed) == 0)
            return "damage_" + name;
        if (!playerModel)
            return null;

        string prefix = ModelSpeciesEffects.Prefix(ModelSpeciesEffects.Parse(species));
        return prefix + "damage_" + name;
    }
}
