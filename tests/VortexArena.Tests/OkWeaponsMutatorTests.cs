using System.Linq;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using Xunit;

namespace VortexArena.Tests;

[Collection("GlobalState")]
public class OkWeaponsMutatorTests : IDisposable
{
    public void Dispose() => MutatorActivation.DeactivateAll();

    [Theory]
    [InlineData(null, false)]
    [InlineData("", true)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("0.500000", true)]
    [InlineData("0.5", false)]
    [InlineData("true", false)]
    [InlineData("condition", true)]
    [InlineData("!condition", false)]
    [InlineData("!unknown", true)]
    [InlineData("+condition !unknown", true)]
    [InlineData("+condition unknown", false)]
    [InlineData("-condition unknown", true)]
    [InlineData("-condition !unknown", false)]
    [InlineData("condition>=2 condition<=2 condition>1 condition<3 condition==2 condition!=3", true)]
    [InlineData("condition>2", false)]
    [InlineData("prefixed==2suffix", true)]
    [InlineData("condition===2", false)]
    [InlineData("condition!==2", false)]
    [InlineData("condition!==0", true)]
    [InlineData("\"multi word>=4\" // ignored", true)]
    public void StandaloneExpressionControlsWeaponSpawnFlags(string? expression, bool enabled)
    {
        Boot();
        Api.Cvars.Set("condition", "2");
        Api.Cvars.Set("multi word", "4");
        Api.Cvars.Set("prefixed", "2suffix");
        if (expression is not null)
            Api.Cvars.Set("g_overkill_weapons", expression);

        MutatorActivation.Apply();

        AssertWeaponsAvailable(enabled);
        Assert.False(Mutators.ByName("overkill")!.Added);
    }

    [Fact]
    public void ReapplyingActivationRespondsToReferencedCvarAndFullMode()
    {
        Boot();
        Api.Cvars.Set("g_overkill_weapons", "condition>0");
        Api.Cvars.Set("condition", "1");
        MutatorActivation.Apply();
        AssertWeaponsAvailable(true);

        Api.Cvars.Set("condition", "0");
        MutatorActivation.Apply();
        AssertWeaponsAvailable(false);

        Api.Cvars.Set("g_overkill", "1");
        Api.Cvars.Set("g_mod_balance", "Overkill");
        MutatorActivation.Apply();
        AssertWeaponsAvailable(true);
    }

    private static void Boot()
    {
        Api.Services = new EngineServices(new CollisionWorld());
        GameRegistries.Reset();
        GameRegistries.Bootstrap();
    }

    private static void AssertWeaponsAvailable(bool available)
    {
        Weapon[] weapons = Weapons.All.Where(w => w.NetName is
            "okmachinegun" or "okhmg" or "oknex" or "okrpc" or "okshotgun").ToArray();
        Assert.Equal(5, weapons.Length);
        Assert.All(weapons, w => Assert.Equal(!available, (w.SpawnFlags & WeaponFlags.MutatorBlocked) != 0));
    }
}
