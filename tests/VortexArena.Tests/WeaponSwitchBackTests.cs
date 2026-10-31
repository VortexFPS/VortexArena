using System;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Gameplay.Damage;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Playtest #70 (fast switching leaves the gun at the bottom of the screen). Base W_WeaponFrame does not cancel
/// a drop: switching A→B→A inside B's WS_DROP only latches the new m_switchweapon, w_clear still empties the slot
/// when the drop ends, and the WS_CLEAR branch raises A again. The first-person view (NetGame.EquipNetworkedWeapon)
/// now mirrors that — finish the lowering, then raise the same gun — instead of dropping the staged switch and
/// leaving the gun parked. This pins the server half it mirrors.
/// </summary>
[Collection("GlobalState")]
public class WeaponSwitchBackTests : IDisposable
{
    public void Dispose() => MutatorActivation.DeactivateAll();

    private sealed class TestFacade : IEngineServices
    {
        public EngineServices Inner { get; } = new(new CollisionWorld());
        public MutableClock GameClock { get; } = new();
        public ITraceService Trace => Inner.Trace;
        public IEntityService Entities => Inner.Entities;
        public ICvarService Cvars => Inner.Cvars;
        public ISoundService Sound => Inner.Sound;
        public IModelService Models => Inner.Models;
        public IGameClock Clock => GameClock;
    }

    [Fact]
    public void Switching_Back_Mid_Drop_Finishes_The_Drop_Then_Raises_The_Same_Weapon()
    {
        var f = new TestFacade();
        foreach (string w in new[] { "devastator", "mortar" })
        {
            f.Cvars.Set($"g_balance_{w}_switchdelay_drop", "0.2"); // bal-wep-xonotic.cfg values
            f.Cvars.Set($"g_balance_{w}_switchdelay_raise", "0.2");
        }
        Api.Services = f;
        VehicleCommon.GameStopped = false;
        GameRegistries.Reset();
        StatusEffectsCatalog.RegisterAll();
        GameRegistries.Bootstrap();
        Combat.System = new DamageSystem();
        MutatorActivation.Apply();

        Weapon a = Weapons.ByName("devastator")!;
        Weapon b = Weapons.ByName("mortar")!;
        Entity p = f.Entities.Spawn();
        p.ClassName = "player";
        p.Flags = EntFlags.Client;
        p.Health = 100;
        p.SetResourceExplicit(ResourceType.Health, 100f);
        p.SetResourceExplicit(ResourceType.Rockets, 100f);
        Inventory.GiveWeapon(p, a);
        Inventory.GiveWeapon(p, b);
        var slot = new WeaponSlot(0);
        var st = p.WeaponState(slot);

        float t = 1f;
        void Tick(int n = 1)
        {
            for (int i = 0; i < n; i++)
            {
                f.GameClock.Time = t;
                WeaponFireDriver.Frame(p, null);
                t += 1f / 72f;
            }
        }

        Inventory.SwitchWeapon(p, a);
        Tick(72); // equip + raise A, settle
        Assert.Equal(a.RegistryId, st.CurrentWeaponId);
        Assert.Equal(WeaponFireState.Ready, st.State);

        Inventory.SwitchWeapon(p, b);
        Tick();
        Assert.Equal(WeaponFireState.Drop, st.State);

        Inventory.SwitchWeapon(p, a); // back, while A is still lowering
        Tick();
        Assert.Equal(WeaponFireState.Drop, st.State); // the drop is not cancelled

        bool raised = false;
        for (int i = 0; i < 72 && !raised; i++)
        {
            Tick();
            raised = st.State == WeaponFireState.Raise;
        }
        Assert.True(raised, "after the drop the same weapon raises again");
        Assert.Equal(a.RegistryId, st.CurrentWeaponId);
        Tick(72);
        Assert.Equal(WeaponFireState.Ready, st.State);
    }
}
