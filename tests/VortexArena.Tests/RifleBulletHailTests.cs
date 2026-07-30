using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Verifies the Rifle held-fire bullethail (T16) — the C# port of W_Rifle_BulletHail/_Continue
/// (common/weapons/weapon/rifle.qc:59-92). With <c>bullethail</c> on, holding primary keeps auto-firing
/// volleys faster than the bare refire would allow (the continuation think resets ATTACK_FINISHED each
/// animtime tick), the burst budget (<c>rifle_accumulator</c>/<c>burstcost</c>) caps the hail, and
/// releasing the button stops it. Runs in the serialized collection (mutates the shared registry /
/// <see cref="Api.Services"/>).
/// </summary>
[Collection("GlobalState")]
public class RifleBulletHailTests
{
    // We count one "volley" per W_Rifle_FireBullet — each plays the rifle fire sound exactly once.
    private const string RifleFireSound = "weapons/campingrifle_fire.wav";

    /// <summary>Configure a rifle whose bullethail is ON, with a LONG refire (so only the hail can refire
    /// between animtime ticks) and a chosen burst budget. Returns the freshly-configured Rifle singleton.</summary>
    private static Rifle ConfigureRifle(BulletHailServices svc, float burstcost, float bursttime)
    {
        GameRegistries.Bootstrap();
        var c = svc.Cvars;
        c.Set("g_balance_rifle_primary_bullethail", "1");
        c.Set("g_balance_rifle_primary_refire", "1.0");     // long: WrThink alone can't refire each 0.1s tick
        c.Set("g_balance_rifle_primary_animtime", "0.1");   // the hail continuation cadence
        c.Set("g_balance_rifle_primary_burstcost", burstcost.ToString(System.Globalization.CultureInfo.InvariantCulture));
        c.Set("g_balance_rifle_bursttime", bursttime.ToString(System.Globalization.CultureInfo.InvariantCulture));
        c.Set("g_balance_rifle_primary_shots", "1");
        c.Set("g_balance_rifle_primary_spread", "0");

        var rifle = (Rifle)Weapons.ByName("rifle")!;
        rifle.Configure();
        return rifle;
    }

    private static Player MakeRifleman(Rifle rifle, BulletHailServices svc)
    {
        var p = new Player { Flags = EntFlags.Client, UnlimitedAmmo = true };
        p.SetResource(ResourceType.Health, 100f);
        p.SetResource(ResourceType.Bullets, 999f);
        Inventory.GiveWeapon(p, rifle); // equips + sets ActiveWeaponId
        // QC wr_resetplayer seeds rifle_accumulator = time - bursttime so the budget starts full; the headless
        // fire driver doesn't run that spawn pass, so seed it here (drained == ready to burst).
        p.WeaponState(new WeaponSlot(0)).RifleAccumulator = svc.Clock.Time - rifle.BurstTime;
        return p;
    }

    [Fact]
    public void Held_Primary_Fires_Multiple_Volleys_Then_Releasing_Stops_It()
    {
        var svc = new BulletHailServices(startTime: 10f, frameTime: 0.1f);
        Api.Services = svc;
        // burstcost 0 -> the budget never gates; isolate the hail-continues behaviour.
        Rifle rifle = ConfigureRifle(svc, burstcost: 0f, bursttime: 0f);
        Player p = MakeRifleman(rifle, svc);

        var held = new MovementInput { ButtonAttack1 = true, FrameTime = 0.1f };

        // Hold fire for 5 ticks. Without the hail, the 1.0s refire would allow only ONE volley in 0.5s;
        // the bullethail continuation re-fires every 0.1s animtime tick.
        for (int i = 0; i < 5; i++)
        {
            WeaponFireDriver.Frame(p, held);
            svc.Clock.Time += 0.1f;
        }
        int heldVolleys = svc.FireCount;
        Assert.True(heldVolleys >= 4, $"bullethail should fire repeatedly while held, got {heldVolleys}");

        // Release fire and keep ticking: the hail must stop (no new volleys).
        var released = new MovementInput { ButtonAttack1 = false, FrameTime = 0.1f };
        for (int i = 0; i < 5; i++)
        {
            WeaponFireDriver.Frame(p, released);
            svc.Clock.Time += 0.1f;
        }
        Assert.Equal(heldVolleys, svc.FireCount); // releasing the button stopped the hail
    }

    [Fact]
    public void Burst_Budget_Gates_The_Hail()
    {
        var svc = new BulletHailServices(startTime: 10f, frameTime: 0.1f);
        Api.Services = svc;
        // A finite burst budget: capacity ~ bursttime/burstcost shots before it must pause to refill.
        Rifle rifle = ConfigureRifle(svc, burstcost: 0.3f, bursttime: 1.0f);
        Player p = MakeRifleman(rifle, svc);

        var held = new MovementInput { ButtonAttack1 = true, FrameTime = 0.1f };

        // Hold fire across 10 animtime ticks. The budget must SKIP some ticks (it can't sustain a shot every
        // 0.1s when each shot costs 0.3 of a 1.0s window), so the volley count is strictly below the tick
        // count — yet still > 1, proving the hail ran past the first shot.
        for (int i = 0; i < 10; i++)
        {
            WeaponFireDriver.Frame(p, held);
            svc.Clock.Time += 0.1f;
        }

        Assert.True(svc.FireCount >= 3, $"hail should fire a burst, got {svc.FireCount}");
        Assert.True(svc.FireCount <= 8, $"burst budget should cap the hail (skip ticks), got {svc.FireCount}");
    }

    [Fact]
    public void Bullethail_Off_Fires_One_Volley_Per_Refire()
    {
        var svc = new BulletHailServices(startTime: 10f, frameTime: 0.1f);
        Api.Services = svc;
        Rifle rifle = ConfigureRifle(svc, burstcost: 0f, bursttime: 0f);
        // turn the hail OFF (regression guard: without it, the long refire limits the rate).
        svc.Cvars.Set("g_balance_rifle_primary_bullethail", "0");
        rifle.Configure();
        Player p = MakeRifleman(rifle, svc);

        var held = new MovementInput { ButtonAttack1 = true, FrameTime = 0.1f };
        // 5 ticks of 0.1s = 0.5s, less than the 1.0s refire -> exactly one volley.
        for (int i = 0; i < 5; i++)
        {
            WeaponFireDriver.Frame(p, held);
            svc.Clock.Time += 0.1f;
        }
        Assert.Equal(1, svc.FireCount);
    }

    // ---- test scaffolding --------------------------------------------------------------------------

    /// <summary>
    /// A minimal <see cref="IEngineServices"/> with a settable <see cref="MutableClock"/>, an empty-world
    /// trace (every bullet misses — we only count fires), a real cvar store (so balance reconfigures), and a
    /// sound service that COUNTS rifle fire sounds (== volleys). Entities/models are benign null impls.
    /// </summary>
    private sealed class BulletHailServices : IEngineServices
    {
        public MutableClock Clock { get; }
        public CountingSound SoundImpl { get; } = new();
        public StoreCvars Cvars { get; } = new();

        public ITraceService Trace { get; } = new MissTrace();
        IGameClock IEngineServices.Clock => Clock;
        ICvarService IEngineServices.Cvars => Cvars;
        ISoundService IEngineServices.Sound => SoundImpl;
        public IEntityService Entities { get; } = new NullEntities();
        public IModelService Models { get; } = new NullModels();

        public int FireCount => SoundImpl.Count(RifleFireSound);

        public BulletHailServices(float startTime, float frameTime)
            => Clock = new MutableClock { Time = startTime, FrameTime = frameTime };

        private sealed class MissTrace : ITraceService
        {
            public TraceResult Trace(Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end, MoveFilter filter, Entity? ignore)
                => TraceResult.Miss(end);
            public int PointContents(Vector3 point) => 0;
            public bool CheckPvs(Vector3 viewpoint, Vector3 target) => true;
        }
        private sealed class NullEntities : IEntityService
        {
            public Entity Spawn() => new();
            public void Remove(Entity e) { }
            public void SetOrigin(Entity e, Vector3 origin) => e.Origin = origin;
            public void SetSize(Entity e, Vector3 mins, Vector3 maxs) { e.Mins = mins; e.Maxs = maxs; }
            public void SetModel(Entity e, string model) { }
            public IEnumerable<Entity> FindByClass(string className) => System.Array.Empty<Entity>();
            public IEnumerable<Entity> FindInRadius(Vector3 origin, float radius) => System.Array.Empty<Entity>();
        }
        private sealed class NullModels : IModelService
        {
            public bool TryGetTag(Entity e, string tagName, out Vector3 origin, out Vector3 forward, out Vector3 right, out Vector3 up)
            { origin = forward = right = up = Vector3.Zero; return false; }
            public void SetAttachment(Entity e, Entity parent, string tagName) { }
        }
    }

    /// <summary>An <see cref="ISoundService"/> that tallies plays by sample name.</summary>
    private sealed class CountingSound : ISoundService
    {
        private readonly Dictionary<string, int> _counts = new();
        public void Play(Entity e, SoundChannel channel, string sample, float volume = 1f, float attenuation = 1f, bool loop = false, float pitch = 1f)
            => _counts[sample] = _counts.TryGetValue(sample, out int n) ? n + 1 : 1;
        public void Stop(Entity e, SoundChannel channel) { }
        public int Count(string sample) => _counts.TryGetValue(sample, out int n) ? n : 0;
    }

    /// <summary>A real dictionary cvar store (so Rifle.Configure reads the values we set).</summary>
    private sealed class StoreCvars : ICvarService
    {
        private readonly Dictionary<string, string> _v = new();
        public float GetFloat(string name) =>
            _v.TryGetValue(name, out var s) && float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f;
        public string GetString(string name) => _v.TryGetValue(name, out var s) ? s : "";
        public void Set(string name, string value) => _v[name] = value;
        public void Register(string name, string defaultValue, CvarFlags flags = CvarFlags.None)
        { if (!_v.ContainsKey(name)) _v[name] = defaultValue; }
    }
}
