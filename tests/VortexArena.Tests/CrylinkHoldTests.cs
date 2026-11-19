using System;
using System.Linq;
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
/// Playtest #73 ("Crylink primary if you keep holding it"). Base (crylink.qc wr_think): after a join-enabled
/// volley (joinspread != 0 &amp;&amp; shots &gt; 1 — the stock primary) <c>crylink_waitrelease</c> blocks that mode until the
/// button is released, however long it is held and whether or not the spikes are still alive. The server matched
/// that; the client's fire prediction did not, and played a flash/animation/sound every refire while held. The
/// prediction now asks <see cref="Crylink.WaitsForRelease"/>; these pin the server rule it mirrors.
/// </summary>
[Collection("GlobalState")]
public class CrylinkHoldTests : IDisposable
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

    private static TestFacade Boot()
    {
        var facade = new TestFacade();
        Api.Services = facade;
        VehicleCommon.GameStopped = false;
        GameRegistries.Reset();
        StatusEffectsCatalog.RegisterAll();
        GameRegistries.Bootstrap();
        Combat.System = new DamageSystem();
        MutatorActivation.Apply();
        return facade;
    }

    [Fact]
    public void Stock_Primary_Waits_For_Release_And_Secondary_Does_Not()
    {
        Boot();
        var cl = (Crylink)Weapons.ByName("crylink")!;
        Assert.True(cl.WaitsForRelease(secondary: false));  // joinspread 0.2, 6 shots
        Assert.False(cl.WaitsForRelease(secondary: true));  // joinspread 0
        cl.PrimaryJoinSpread = 0f;
        Assert.False(cl.WaitsForRelease(secondary: false)); // joinspread 0 → refires while held, as in Base
    }

    [Fact]
    public void Held_Primary_Fires_Once_Even_After_The_Spikes_Die_Until_Released()
    {
        TestFacade f = Boot();
        var cl = (Crylink)Weapons.ByName("crylink")!;
        Entity actor = f.Entities.Spawn();
        actor.ClassName = "player";
        actor.Flags = EntFlags.Client;
        actor.Mins = new Vector3(-16, -16, -24);
        actor.Maxs = new Vector3(16, 16, 45);
        actor.Health = 100;
        f.Entities.SetOrigin(actor, Vector3.Zero);
        actor.ActiveWeaponId = cl.RegistryId;
        actor.SetResourceExplicit(ResourceType.Cells, 999f);
        var slot = new WeaponSlot(0);
        var st = actor.WeaponState(slot);
        st.State = WeaponFireState.Ready;
        st.AttackFinished = 0f;

        int Volleys() => f.Entities.FindByClass("spike").Count(e => !e.IsFreed);

        f.GameClock.Time = 1f;
        st.ButtonAttack = true;
        cl.WrThink(actor, slot, FireMode.Primary);
        int first = Volleys();
        Assert.True(first > 1);
        Assert.Equal(1, st.CrylinkWaitRelease);

        // Kill the volley (as if every spike expired) and keep holding well past the refire: Base still waits.
        foreach (Entity s in f.Entities.FindByClass("spike").ToList())
            f.Entities.Remove(s);
        for (int i = 0; i < 20; i++)
        {
            f.GameClock.Time += 0.25f;
            st.State = WeaponFireState.Ready;
            st.ButtonAttack = true;
            cl.WrThink(actor, slot, FireMode.Primary);
        }
        Assert.Equal(0, Volleys());

        // Release, then press again: the next volley fires.
        f.GameClock.Time += 0.25f;
        st.ButtonAttack = false;
        cl.WrThink(actor, slot, FireMode.Primary);
        Assert.Equal(0, st.CrylinkWaitRelease);
        f.GameClock.Time += 0.25f;
        st.State = WeaponFireState.Ready;
        st.ButtonAttack = true;
        cl.WrThink(actor, slot, FireMode.Primary);
        Assert.Equal(first, Volleys());
    }
}
