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
/// Playtest #86 ("can't lay more than 4 mines at a time"): Base checks g_balance_minelayer_limit inside
/// W_MineLayer_Attack, i.e. only after weapon_prepareattack let a press through, so the "You cannot place more
/// than 4 mines" center-print + UNAVAILABLE cue fire at most once per refire, and only while attacking. The port
/// checked the limit at the top of WrThink(Primary), which the fire driver calls EVERY tick whether or not the
/// button is held: with four mines out the notification was sent every tick.
/// </summary>
[Collection("GlobalState")]
public class MinelayerLimitTests : IDisposable
{
    private readonly INotificationSink _prevSink = NotificationSystem.Sink;
    private readonly NotificationSystem.RecordingSink _sink = new();

    public void Dispose()
    {
        NotificationSystem.Sink = _prevSink;
        MutatorActivation.DeactivateAll();
    }

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

    private TestFacade Boot()
    {
        var facade = new TestFacade();
        Api.Services = facade;
        VehicleCommon.GameStopped = false;
        GameRegistries.Reset();
        StatusEffectsCatalog.RegisterAll();
        GameRegistries.Bootstrap();
        Notifications.RegisterAll();
        // World boot (Scores.SubscribeToDeaths) back-fills MULTI/CHOICE arg counts from their children; without
        // it a MULTI send with args is rejected as an argument mismatch.
        DeathMessages.ResetChoiceArgCounts();
        DeathMessages.EnsureChoiceArgCounts();
        Combat.System = new DamageSystem();
        MutatorActivation.Apply();
        NotificationSystem.Sink = _sink;
        return facade;
    }

    private static Entity NewPlayer(TestFacade f)
    {
        Entity e = f.Entities.Spawn();
        e.ClassName = "player";
        e.Flags = EntFlags.Client;
        e.Mins = new Vector3(-16, -16, -24);
        e.Maxs = new Vector3(16, 16, 45);
        e.Health = 100; e.MaxHealth = 100;
        e.TakeDamage = DamageMode.Yes;
        f.Entities.SetOrigin(e, Vector3.Zero);
        return e;
    }

    private static void Arm(Entity actor, Weapon w, WeaponSlot slot, bool held)
    {
        actor.ActiveWeaponId = w.RegistryId;
        actor.SetResourceExplicit(ResourceType.Rockets, 999f);
        var st = actor.WeaponState(slot);
        st.State = WeaponFireState.Ready;
        st.AttackFinished = 0f;
        st.ButtonAttack = held;
    }

    // The MULTI fans out to its INFO (console) and CENTER parts; count the center-prints.
    private int LimitNotices() => _sink.Log.Count(d => d.Notification.Type == MsgType.Center && d.Notification.Name.Contains("MINELAYER_LIMIT"));

    [Fact]
    public void Limit_Comes_From_The_Active_Balance_And_Refuses_The_Fifth_Mine_With_One_Notice()
    {
        var f = Boot();
        f.GameClock.Time = 5f;
        var ml = (Minelayer)Weapons.ByName("minelayer")!;
        Assert.Equal(4, ml.Cvars.Limit); // bal-wep-xonotic.cfg: g_balance_minelayer_limit 4

        Entity actor = NewPlayer(f);
        var slot = new WeaponSlot(0);
        for (int i = 0; i < 4; i++)
        {
            Arm(actor, ml, slot, held: true);
            ml.WrThink(actor, slot, FireMode.Primary);
        }
        Assert.Equal(4, f.Entities.FindByClass("mine").Count(e => !e.IsFreed && e.Owner == actor));
        Assert.Equal(0, LimitNotices());

        // Ticks with the button UP at the limit: Base says nothing (the check lives behind prepareattack).
        Arm(actor, ml, slot, held: false);
        for (int i = 0; i < 10; i++)
            ml.WrThink(actor, slot, FireMode.Primary);
        Assert.Equal(0, LimitNotices());

        // A press at the limit: refused, one notice carrying the limit, no fifth mine.
        Arm(actor, ml, slot, held: true);
        ml.WrThink(actor, slot, FireMode.Primary);
        Assert.Equal(4, f.Entities.FindByClass("mine").Count(e => !e.IsFreed && e.Owner == actor));
        Assert.Equal(1, LimitNotices());
        Assert.Contains(_sink.Log, d => d.Notification.Name.Contains("MINELAYER_LIMIT") && d.FloatArgs.Contains(4f));

        // Holding the button: the refire timer burned by that press throttles the notice (once per refire).
        for (int i = 0; i < 10; i++)
            ml.WrThink(actor, slot, FireMode.Primary);
        Assert.Equal(1, LimitNotices());
    }
}
