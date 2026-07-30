using XonoticGodot.Common.Gameplay;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for §4.4: the central weapon NetName→ammo-type / superweapon registry. <c>AmmoType</c> now lives on
/// the <see cref="Weapon"/> base (was a shadowing per-subclass field), so it is queryable polymorphically, and
/// the superweapon flag is exposed via <see cref="Weapon.IsSuperWeapon"/>.
/// </summary>
public class WeaponAmmoTests
{
    [Fact]
    public void AmmoType_IsSetPerWeapon_ViaBaseField()
    {
        Assert.Equal(ResourceType.Cells, new Vortex().AmmoType);
        Assert.Equal(ResourceType.Cells, new Vaporizer().AmmoType);
        Assert.Equal(ResourceType.Rockets, new Devastator().AmmoType);
        Assert.Equal(ResourceType.Bullets, new Machinegun().AmmoType);
        Assert.Equal(ResourceType.Shells, new Shotgun().AmmoType);
        Assert.Equal(ResourceType.Fuel, new Hook().AmmoType);
    }

    [Fact]
    public void AmmolessWeapons_ReportNone()
    {
        Assert.Equal(ResourceType.None, new Blaster().AmmoType);
        Assert.Equal(ResourceType.None, new Porto().AmmoType);
        Assert.Equal(ResourceType.None, new Tuba().AmmoType);
        Assert.Equal(ResourceType.None, new Fireball().AmmoType);
    }

    [Fact]
    public void SuperWeaponFlag_IsExposed()
    {
        Assert.True(new Fireball().IsSuperWeapon);
        Assert.True(new Vaporizer().IsSuperWeapon);
        Assert.True(new Porto().IsSuperWeapon);
        Assert.False(new Vortex().IsSuperWeapon);
        Assert.False(new Blaster().IsSuperWeapon);
    }
}
