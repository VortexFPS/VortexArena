using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Gameplay.Damage;
using XonoticGodot.Common.Services;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Verifies the headshot port (T16): the head-AABB + ray-box math (<see cref="WeaponFiring.Headshot"/> /
/// <see cref="WeaponFiring.TraceHitsBox"/>, ports of server/weapons/tracing.qc Headshot + common/util.qc
/// trace_hits_box) and that a hitscan bullet through the head box scales damage by the weapon's
/// headshot_multiplier BEFORE the Damage() call (QC tracing.qc:441-445). Runs in the serialized collection
/// because it installs the ambient <see cref="Api.Services"/> / <see cref="Combat.System"/>.
/// </summary>
[Collection("GlobalState")]
public class HeadshotTests
{
    public HeadshotTests()
    {
        // The announce path resolves ANNCE_HEADSHOT from the notification registry, and Headshot() probes the
        // Frozen status-effect; register both (idempotent) so those lookups resolve.
        Notifications.RegisterAll();
        StatusEffectsCatalog.RegisterAll();
    }

    // A living, damageable player whose head box (QC: horizontally 0.6*body, vertically
    // 1.3*view_ofs_z .. maxs_z) straddles the test ray. With these numbers the head box is
    // X[90.4,109.6] Y[-9.6,9.6] Z[12.5,45], centred on the origin at (100,0,0).
    private static Entity MakeTarget()
    {
        var t = new Entity
        {
            Flags = EntFlags.Client,
            TakeDamage = DamageMode.Yes,
            Origin = new Vector3(100, 0, 0),
            Mins = new Vector3(-16, -16, -24),
            Maxs = new Vector3(16, 16, 45),
            ViewOfs = new Vector3(0, 0, 20),
        };
        t.SetResource(ResourceType.Health, 100f); // not needed for Headshot() but realistic
        return t;
    }

    // ---- trace_hits_box (pure math) ----------------------------------------------------------------

    [Fact]
    public void TraceHitsBox_Hits_When_Segment_Passes_Through()
    {
        var lo = new Vector3(-1, -1, -1);
        var hi = new Vector3(1, 1, 1);
        // a ray straight through the centre of the box
        Assert.True(WeaponFiring.TraceHitsBox(new Vector3(-5, 0, 0), new Vector3(5, 0, 0), lo, hi));
        // a ray that starts inside the box
        Assert.True(WeaponFiring.TraceHitsBox(Vector3.Zero, new Vector3(5, 0, 0), lo, hi));
    }

    [Fact]
    public void TraceHitsBox_Misses_When_Segment_Is_Off_To_The_Side()
    {
        var lo = new Vector3(-1, -1, -1);
        var hi = new Vector3(1, 1, 1);
        // parallel to X but offset in Z above the box -> never enters
        Assert.False(WeaponFiring.TraceHitsBox(new Vector3(-5, 0, 5), new Vector3(5, 0, 5), lo, hi));
        // the segment stops short of the box (ends before reaching it)
        Assert.False(WeaponFiring.TraceHitsBox(new Vector3(-5, 0, 0), new Vector3(-3, 0, 0), lo, hi));
    }

    // ---- Headshot (head-AABB on a player) ----------------------------------------------------------

    [Fact]
    public void Headshot_True_For_Ray_Through_The_Head_Box()
    {
        Entity targ = MakeTarget();
        var attacker = new Entity { Flags = EntFlags.Client };
        // ray along +X at Z=30 (inside head Z[12.5,45]) and Y=0 -> passes through the head box.
        Assert.True(WeaponFiring.Headshot(targ, attacker, new Vector3(0, 0, 30), new Vector3(1000, 0, 30)));
    }

    [Fact]
    public void Headshot_False_For_Body_Or_Leg_Ray()
    {
        Entity targ = MakeTarget();
        var attacker = new Entity { Flags = EntFlags.Client };
        // ray along +X at Z=0 (below head Z[12.5,45]) -> body/leg shot, not a headshot.
        Assert.False(WeaponFiring.Headshot(targ, attacker, new Vector3(0, 0, 0), new Vector3(1000, 0, 0)));
    }

    [Fact]
    public void Headshot_False_When_Dead_Frozen_Or_Not_Damageable()
    {
        var attacker = new Entity { Flags = EntFlags.Client };
        Vector3 s = new(0, 0, 30), e = new(1000, 0, 30);

        Entity dead = MakeTarget();
        dead.DeadState = DeadFlag.Dead;                 // IS_DEAD -> no head box
        Assert.False(WeaponFiring.Headshot(dead, attacker, s, e));

        Entity frozen = MakeTarget();
        frozen.FrozenStat = 1;                          // STAT(FROZEN) -> no head box
        Assert.False(WeaponFiring.Headshot(frozen, attacker, s, e));

        Entity invuln = MakeTarget();
        invuln.TakeDamage = DamageMode.No;              // !takedamage -> no head box
        Assert.False(WeaponFiring.Headshot(invuln, attacker, s, e));

        Entity notPlayer = MakeTarget();
        notPlayer.Flags = EntFlags.None;                // !IS_PLAYER -> no head box
        Assert.False(WeaponFiring.Headshot(notPlayer, attacker, s, e));
    }

    // ---- FireBullet applies the headshot multiplier before Damage() --------------------------------

    [Fact]
    public void FireBullet_Scales_Damage_By_Multiplier_On_A_Head_Hit()
    {
        var dmg = new RecordingDamage();
        using var _ = new HeadshotEnv(MakeTarget(), out Entity target, dmg);

        var actor = new Entity { Flags = EntFlags.Client };
        // ray at Z=30 (through the head box). solidPenetration -1 == single hit, headshotMultiplier 2.
        WeaponFiring.FireBullet(actor, new Vector3(0, 0, 30), new Vector3(1, 0, 0), range: 1000f,
            damage: 50f, deathType: 0, spread: 0f, solidPenetration: -1f, headshotMultiplier: 2f);

        Assert.Single(dmg.Events);
        Assert.Same(target, dmg.Events[0].Target);
        Assert.Equal(100f, dmg.Events[0].Amount, 3); // 50 * 2 (head)
    }

    [Fact]
    public void FireBullet_Does_Not_Scale_On_A_Body_Hit()
    {
        var dmg = new RecordingDamage();
        using var env = new HeadshotEnv(MakeTarget(), out _, dmg);

        var actor = new Entity { Flags = EntFlags.Client };
        // ray at Z=0 (below the head box) -> body hit, no multiplier even though one is set.
        WeaponFiring.FireBullet(actor, new Vector3(0, 0, 0), new Vector3(1, 0, 0), range: 1000f,
            damage: 50f, deathType: 0, spread: 0f, solidPenetration: -1f, headshotMultiplier: 2f);

        Assert.Single(dmg.Events);
        Assert.Equal(50f, dmg.Events[0].Amount, 3); // base damage, unscaled
    }

    [Fact]
    public void FireBullet_Announces_Headshot_To_The_Shooter()
    {
        var dmg = new RecordingDamage();
        using var env = new HeadshotEnv(MakeTarget(), out _, dmg);
        NotificationSystem.Sink = NotificationSystem.Recorder;
        NotificationSystem.Recorder.Clear();

        var actor = new Entity { Flags = EntFlags.Client };
        WeaponFiring.FireBullet(actor, new Vector3(0, 0, 30), new Vector3(1, 0, 0), range: 1000f,
            damage: 50f, deathType: 0, spread: 0f, solidPenetration: -1f, headshotMultiplier: 2f);

        Assert.Contains(NotificationSystem.Recorder.Log, d => d.Notification.RegistryName.Contains("HEADSHOT"));
    }

    // ---- test scaffolding --------------------------------------------------------------------------

    /// <summary>Records every <see cref="Combat.Damage"/> the bullet pipeline applies, with the dealt amount.</summary>
    private sealed class RecordingDamage : IDamageSystem
    {
        public readonly List<DamageInfo> Events = new();
        public float Apply(in DamageInfo info) { Events.Add(info); return info.Amount; }
    }

    /// <summary>
    /// A trace service whose single solid target sits at <see cref="_target"/>: the first trace from a start
    /// reports a hit on it (endpos a little along the ray, still inside the box), later traces miss. Good
    /// enough to drive <see cref="WeaponFiring.FireBullet"/>'s per-hit block deterministically.
    /// </summary>
    private sealed class OneHitTrace : ITraceService
    {
        private readonly Entity _target;
        private bool _used;
        public OneHitTrace(Entity target) => _target = target;

        public TraceResult Trace(Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end, MoveFilter filter, Entity? ignore)
        {
            if (_used) return TraceResult.Miss(end);
            _used = true;
            Vector3 dir = end - start;
            float len = dir.Length();
            // Land the hit point ~at the target's X plane along the ray, so Headshot's segment from here still
            // passes through the head box for the head-Z ray (and is a plain body hit for the body-Z ray).
            Vector3 hitPos = (len > 0f)
                ? start + dir / len * MathF.Min(100f, len)
                : start;
            return new TraceResult { Fraction = 0.1f, EndPos = hitPos, Ent = _target, PlaneNormal = -Vector3.UnitX };
        }

        public int PointContents(Vector3 point) => 0;
        public bool CheckPvs(Vector3 viewpoint, Vector3 target) => true;
    }

    /// <summary>Installs <see cref="Api.Services"/> (one-hit trace) + a recording damage system; restores on dispose.</summary>
    private sealed class HeadshotEnv : System.IDisposable
    {
        private readonly IDamageSystem _prevDamage;
        private readonly IEngineServices _prevServices;

        public HeadshotEnv(Entity target, out Entity outTarget, IDamageSystem damage)
        {
            outTarget = target;
            _prevServices = Api.Services;
            _prevDamage = Combat.System;
            Api.Services = new HeadshotServices(new OneHitTrace(target));
            Combat.System = damage;
        }

        public void Dispose()
        {
            Api.Services = _prevServices;
            Combat.System = _prevDamage;
        }
    }

    /// <summary>A minimal <see cref="IEngineServices"/> with a custom trace; other services are benign null impls.</summary>
    private sealed class HeadshotServices : IEngineServices
    {
        public ITraceService Trace { get; }
        public IGameClock Clock { get; } = new ZeroClock();
        public ICvarService Cvars { get; } = new EmptyCvars();
        public IEntityService Entities { get; } = new NullEntities();
        public ISoundService Sound { get; } = new NullSound();
        public IModelService Models { get; } = new NullModels();
        public HeadshotServices(ITraceService trace) => Trace = trace;

        private sealed class ZeroClock : IGameClock { public float Time => 0f; public float FrameTime => 1f / 72f; }
        private sealed class EmptyCvars : ICvarService
        {
            public float GetFloat(string name) => 0f;
            public string GetString(string name) => "";
            public void Set(string name, string value) { }
            public void Register(string name, string defaultValue, CvarFlags flags = CvarFlags.None) { }
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
        private sealed class NullSound : ISoundService
        {
            public void Play(Entity e, SoundChannel channel, string sample, float volume = 1f, float attenuation = 1f, bool loop = false, float pitch = 1f) { }
            public void Stop(Entity e, SoundChannel channel) { }
        }
        private sealed class NullModels : IModelService
        {
            public bool TryGetTag(Entity e, string tagName, out Vector3 origin, out Vector3 forward, out Vector3 right, out Vector3 up)
            { origin = forward = right = up = Vector3.Zero; return false; }
            public void SetAttachment(Entity e, Entity parent, string tagName) { }
        }
    }
}
