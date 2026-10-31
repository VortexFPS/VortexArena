using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Simulation;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Playtest #88(a): the grappling hook must not anchor on the sky. Base's GrapplingHookTouch starts with
/// PROJECTILE_TOUCH, whose SUB_NoImpactCheck reads the touch trace's <c>trace_dphitq3surfaceflags</c> and removes
/// the hook on a NOIMPACT (sky) face. The port's touch delegates now see that trace through <see cref="TouchTrace"/>,
/// published by the engine's SV_Impact port.
/// </summary>
[Collection("GlobalState")]
public class HookSkyTests
{
    private static TraceResult Hit(int surfaceFlags) => new()
    {
        Fraction = 0.5f,
        EndPos = new Vector3(100, 0, 0),
        PlaneNormal = new Vector3(-1, 0, 0),
        PlaneDist = -100f,
        DpHitQ3SurfaceFlags = surfaceFlags,
    };

    [Fact]
    public void Impact_Publishes_The_Trace_To_Both_Touches_And_Clears_It_After()
    {
        var ctx = new PhysicsContext(new FixedTrace(TraceResult.Miss(Vector3.Zero)), _ => { });
        TraceResult seenByMover = default, seenByOther = default;
        bool moverValid = false, otherValid = false;
        var mover = new Entity { Solid = Solid.Corpse };
        mover.Touch = (_, _) => moverValid = TouchTrace.TryGet(out seenByMover);
        var other = new Entity { Solid = Solid.Bsp };
        other.Touch = (_, _) => otherValid = TouchTrace.TryGet(out seenByOther);

        ctx.Impact(mover, Hit(Projectiles.Q3SurfaceFlagNoImpact), other);

        Assert.True(moverValid);
        Assert.Equal(Projectiles.Q3SurfaceFlagNoImpact, seenByMover.DpHitQ3SurfaceFlags);
        Assert.Equal(new Vector3(-1, 0, 0), seenByMover.PlaneNormal);
        Assert.True(otherValid);
        Assert.Equal(new Vector3(1, 0, 0), seenByOther.PlaneNormal); // DP negates the plane for e2
        Assert.False(TouchTrace.TryGet(out _));
    }

    [Fact]
    public void NoImpactCheck_Reads_The_Touch_Trace()
    {
        using var env = new Env(TraceResult.Miss(Vector3.Zero));
        var proj = new Entity();
        using (TouchTrace.Begin(Hit(Projectiles.Q3SurfaceFlagNoImpact)))
            Assert.True(Projectiles.NoImpactCheck(proj, new Entity { ClassName = "worldspawn" }));
        using (TouchTrace.Begin(Hit(0)))
            Assert.False(Projectiles.NoImpactCheck(proj, new Entity { ClassName = "worldspawn" }));
    }

    [Fact]
    public void NoImpactCheck_Reprobes_A_World_Contact_Like_Base()
    {
        // No touch trace (e.g. a touch raised outside a movement impact): Base's fallback traceline one frame of
        // travel either side of the origin finds the sky face.
        using var env = new Env(Hit(Projectiles.Q3SurfaceFlagNoImpact));
        var proj = new Entity { Mins = new Vector3(-3), Maxs = new Vector3(3), Velocity = new Vector3(1800, 0, 0) };
        Assert.True(Projectiles.NoImpactCheck(proj, new Entity { ClassName = "worldspawn" }));
        // A zero-size projectile skips the re-probe, as in Base.
        var point = new Entity { Velocity = new Vector3(1800, 0, 0) };
        Assert.False(Projectiles.NoImpactCheck(point, new Entity { ClassName = "worldspawn" }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Hook_Touching_Sky_Is_Removed_And_Touching_A_Wall_Latches(bool sky)
    {
        using var env = new Env(TraceResult.Miss(Vector3.Zero));
        var hookWeapon = new Hook();
        var slot = new WeaponSlot(0);
        var actor = new Entity { Flags = EntFlags.Client, MoveType = MoveType.Walk };
        var chain = new Entity { ClassName = "grapplinghook", Owner = actor, MoveType = MoveType.Fly, Velocity = new Vector3(1800, 0, 0) };
        actor.WeaponState(slot).Hook = chain;
        MethodInfo touch = typeof(Hook).GetMethod("GrapplingHookTouch", BindingFlags.Instance | BindingFlags.NonPublic)!;

        using (TouchTrace.Begin(Hit(sky ? Projectiles.Q3SurfaceFlagNoImpact : 0)))
            touch.Invoke(hookWeapon, new object[] { chain, new Entity { ClassName = "worldspawn", Solid = Solid.Bsp }, actor, slot });

        if (sky)
        {
            Assert.Null(actor.WeaponState(slot).Hook);
            Assert.Contains(chain, env.Removed);
        }
        else
        {
            Assert.Same(chain, actor.WeaponState(slot).Hook);
            Assert.Equal(MoveType.None, chain.MoveType); // GrapplingHook_Stop: latched
            Assert.DoesNotContain(chain, env.Removed);
        }
    }

    // ---- scaffolding -------------------------------------------------------------------------------

    private sealed class FixedTrace : ITraceService
    {
        private readonly TraceResult _r;
        public FixedTrace(TraceResult r) => _r = r;
        public TraceResult Trace(Vector3 start, Vector3 mins, Vector3 maxs, Vector3 end, MoveFilter filter, Entity? ignore) => _r;
        public int PointContents(Vector3 point) => 0;
        public bool CheckPvs(Vector3 viewpoint, Vector3 target) => true;
    }

    private sealed class Env : IDisposable, IEngineServices
    {
        private readonly IEngineServices _prev;
        public readonly List<Entity> Removed = new();
        public Env(TraceResult trace)
        {
            Trace = new FixedTrace(trace);
            Entities = new RecordingEntities(Removed);
            _prev = Api.Services;
            Api.Services = this;
        }
        public void Dispose() => Api.Services = _prev;

        public ITraceService Trace { get; }
        public IEntityService Entities { get; }
        public ICvarService Cvars { get; } = new EmptyCvars();
        public ISoundService Sound { get; } = new NullSound();
        public IModelService Models { get; } = new NullModels();
        public IGameClock Clock { get; } = new ZeroClock();
    }

    private sealed class RecordingEntities : IEntityService
    {
        private readonly List<Entity> _removed;
        public RecordingEntities(List<Entity> removed) => _removed = removed;
        public Entity Spawn() => new();
        public void Remove(Entity e) => _removed.Add(e);
        public void SetOrigin(Entity e, Vector3 origin) => e.Origin = origin;
        public void SetSize(Entity e, Vector3 mins, Vector3 maxs) { e.Mins = mins; e.Maxs = maxs; }
        public void SetModel(Entity e, string model) { }
        public IEnumerable<Entity> FindByClass(string className) => Array.Empty<Entity>();
        public IEnumerable<Entity> FindInRadius(Vector3 origin, float radius) => Array.Empty<Entity>();
    }

    private sealed class EmptyCvars : ICvarService
    {
        public float GetFloat(string name) => 0f;
        public string GetString(string name) => "";
        public void Set(string name, string value) { }
        public void Register(string name, string defaultValue, CvarFlags flags = CvarFlags.None) { }
    }

    private sealed class ZeroClock : IGameClock { public float Time => 0f; public float FrameTime => 1f / 72f; }

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
