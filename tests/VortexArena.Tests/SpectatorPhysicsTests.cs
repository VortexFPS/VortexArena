using System.Collections.Generic;
using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T44 — physics override hooks (PM_Physics / IsFlying) + spectator free-flight. Verifies the C# port of
/// the three Base behaviors (qcsrc/ecs/systems/physics.qc:108/113 + qcsrc/ecs/systems/sv_physics.qc:67-103):
///   * PM_Physics fires SECOND in the branch chain and a true return FULLY REPLACES the move.
///   * IsFlying is the last `||` term of the noclip/fly branch and a true return FORCES the fly branch.
///   * the spectator-speed ladder seeds STAT(SPECTATORSPEED) and steps it on impulses 1-19/200-229, gated
///     on "was a spectator last tick" (QC lastclassname != STR_PLAYER), and the fly branch is scaled by it.
///
/// Uses the same <see cref="AnalyticWorld"/>/<see cref="MutableClock"/> harness as
/// <see cref="MovementParityTests"/>; the new PMPhysics/IsFlying chains are Clear()-ed up front so the suite
/// stays hermetic regardless of test order (mirroring the parity tests' Clear of the existing chains).
/// </summary>
public class SpectatorPhysicsTests : System.IDisposable
{
    // Reset ALL movement hook chains (the three the parity tests reset PLUS the two T44 chains) so a handler
    // left on a global chain by another test (or an earlier case here) can't leak into the next sim.
    private static void ResetHooks()
    {
        MutatorHooks.PlayerJump.Clear();
        MutatorHooks.PlayerCanCrouch.Clear();
        MutatorHooks.PlayerPhysics.Clear();
        MutatorHooks.PMPhysics.Clear();
        MutatorHooks.IsFlying.Clear();
    }

    // xUnit disposes the fixture after EACH test. Clear the new T44 chains so a handler this test registered
    // can't leak into MovementParityTests/DeterminismTests (which clear PlayerJump/PlayerCanCrouch/PlayerPhysics
    // but NOT PMPhysics/IsFlying). With the chains empty by default the Count>0 guard keeps those golden traces
    // unaffected — this Dispose is the belt-and-suspenders that guarantees it across test order.
    public void Dispose() => ResetHooks();

    /// <summary>Open-air analytic world (no geometry) + settable clock, installed as the ambient services.</summary>
    private static (MutableClock clock, PlayerPhysics physics) FreshWorld(ICvarService? cvars = null)
    {
        ResetHooks();
        var world = AnalyticWorld.FromPlanes(System.Array.Empty<(int, float[])>());
        var clock = new MutableClock();
        Api.Services = cvars is null
            ? new MovementTestServices(world, clock)
            : new SpectatorTestServices(world, clock, cvars);
        return (clock, new PlayerPhysics());
    }

    private const float Dt = 1f / 32f;

    private static MovementInput ForwardInput(float forward = 360f, float up = 0f, int impulse = 0) => new()
    {
        ViewAngles = Vector3.Zero,
        MoveValues = new Vector3(forward, 0f, up),
        FrameTime = Dt,
        Impulse = impulse,
    };

    // ===========================================================================================
    //  PM_Physics hook
    // ===========================================================================================

    [Fact]
    public void PM_Physics_Hook_FullyReplacesMove()
    {
        (MutableClock clock, PlayerPhysics physics) = FreshWorld();

        // a grounded player moving forward
        var player = new Entity
        {
            Origin = new Vector3(0f, 0f, 24.125f),
            Velocity = Vector3.Zero,
            Mins = new Vector3(-16f, -16f, -24f),
            Maxs = new Vector3(16f, 16f, 45f),
            Gravity = 1f,
            Flags = EntFlags.OnGround | EntFlags.JumpReleased,
        };

        // sentinel: the handler "fully replaces" the move by teleporting + setting a marker velocity.
        var sentinelVel = new Vector3(11f, 22f, 33f);
        var sentinelOrigin = new Vector3(500f, 0f, 24.125f);
        MutatorHooks.PMPhysics.Add((ref MutatorHooks.PMPhysicsArgs a) =>
        {
            a.Player.Velocity = sentinelVel;
            a.Player.Origin = sentinelOrigin;
            return true; // FULLY HANDLED — the branch chain must be skipped
        });

        clock.Time += Dt;
        physics.Move(player, ForwardInput());

        // ground branch never ran: velocity/origin are exactly what the mutator set (postupdate doesn't touch them).
        Assert.Equal(sentinelVel, player.Velocity);
        Assert.Equal(sentinelOrigin, player.Origin);

        // remove the handler → the real ground move resumes (player accelerates forward from rest).
        MutatorHooks.PMPhysics.Clear();
        player.Velocity = Vector3.Zero;
        player.Origin = new Vector3(0f, 0f, 24.125f);
        clock.Time += Dt;
        physics.Move(player, ForwardInput());
        Assert.True(player.Velocity.X > 0f, "ground branch should accelerate forward once the hook is gone");
    }

    [Fact]
    public void PM_Physics_Hook_FalseReturn_NormalMove()
    {
        // control run: empty chain.
        (MutableClock clockA, PlayerPhysics physA) = FreshWorld();
        Entity control = GroundPlayer();
        clockA.Time += Dt;
        physA.Move(control, ForwardInput());
        Vector3 controlOrigin = control.Origin, controlVel = control.Velocity;

        // a PM_Physics handler that returns FALSE must leave the move identical (chain ORs to false → fall through).
        (MutableClock clockB, PlayerPhysics physB) = FreshWorld();
        bool called = false;
        MutatorHooks.PMPhysics.Add((ref MutatorHooks.PMPhysicsArgs a) => { called = true; return false; });
        Entity p = GroundPlayer();
        clockB.Time += Dt;
        physB.Move(p, ForwardInput());

        Assert.True(called, "the PMPhysics handler should still be invoked");
        AssertClose(controlOrigin, p.Origin, "origin must match the no-handler control");
        AssertClose(controlVel, p.Velocity, "velocity must match the no-handler control");
    }

    // ===========================================================================================
    //  IsFlying hook
    // ===========================================================================================

    [Fact]
    public void IsFlying_Hook_ForcesFlyBranch()
    {
        // Distinguisher: the fly branch uses the FULL-3D wish velocity (so up-input → upward velocity), while
        // the ground branch's 2D wishdir ignores move.z entirely (z is gravity/jump only). In open air with
        // OnGround set at entry the control takes the ground branch (no gravity on ground) → no upward velocity;
        // the IsFlying-forced run takes the fly branch → upward velocity from move.z.

        // control: walking player, empty chain → ground branch, up-input ignored.
        (MutableClock clockA, PlayerPhysics physA) = FreshWorld();
        Entity control = GroundPlayer();
        control.MoveType = MoveType.Walk;
        clockA.Time += Dt;
        physA.Move(control, ForwardInput(forward: 0f, up: 360f));
        Assert.True(control.Velocity.Z <= 0.001f,
            $"ground branch must ignore up-input (z stays ~0), got {control.Velocity.Z}");

        // forced fly: same setup but an IsFlying handler returns true.
        (MutableClock clockB, PlayerPhysics physB) = FreshWorld();
        MutatorHooks.IsFlying.Add((ref MutatorHooks.IsFlyingArgs a) => true);
        Entity p = GroundPlayer();
        p.MoveType = MoveType.Walk; // NOT a noclip/fly movetype — the hook alone forces the branch
        clockB.Time += Dt;
        physB.Move(p, ForwardInput(forward: 0f, up: 360f));

        Assert.True(p.Velocity.Z > 0f,
            $"IsFlying hook must force the fly branch (full-3D accel → upward velocity), got {p.Velocity.Z}");
        Assert.False(p.OnGround, "the fly branch UNSETs onground");
    }

    // ===========================================================================================
    //  Spectator free-flight speed ladder
    // ===========================================================================================

    [Fact]
    public void Spectator_SpeedLadder_StepsUp()
    {
        var cvars = new DictCvars();
        cvars.Set("sv_spectator_speed_multiplier", "2"); // base/reset value
        (MutableClock clock, PlayerPhysics physics) = FreshWorld(cvars);

        var spec = SpectatorObserver();
        spec.SpectatorSpeed = 1f;        // a known base on the ladder
        spec.WasSpectatorLastTick = true; // already a spectator last tick → the ladder STEPS

        // impulse 10 → +0.5 bounded [min=1, max=5]
        clock.Time += Dt;
        physics.Move(spec, ForwardInput(impulse: 10));
        Assert.Equal(1.5f, spec.SpectatorSpeed, 3);

        // repeat impulse 10 until it clamps at max (5)
        for (int i = 0; i < 20; i++)
        {
            clock.Time += Dt;
            physics.Move(spec, ForwardInput(impulse: 10));
        }
        Assert.Equal(5f, spec.SpectatorSpeed, 3);

        // impulse 12 steps DOWN by 0.5
        clock.Time += Dt;
        physics.Move(spec, ForwardInput(impulse: 12));
        Assert.Equal(4.5f, spec.SpectatorSpeed, 3);

        // impulse 11 RESETS to the multiplier (2)
        clock.Time += Dt;
        physics.Move(spec, ForwardInput(impulse: 11));
        Assert.Equal(2f, spec.SpectatorSpeed, 3);

        // impulse 5 sets 1 + 0.5*(5-1) = 3
        clock.Time += Dt;
        physics.Move(spec, ForwardInput(impulse: 5));
        Assert.Equal(3f, spec.SpectatorSpeed, 3);
    }

    [Fact]
    public void Spectator_SpeedLadder_FirstTickNoStep()
    {
        // QC sv_physics.qc:76 — the ladder only steps when `lastclassname != STR_PLAYER` (was already a
        // spectator). A freshly-un-spawned spectator (WasSpectatorLastTick=false) only SEEDS + clears, no step.
        var cvars = new DictCvars();
        cvars.Set("sv_spectator_speed_multiplier", "2");
        (MutableClock clock, PlayerPhysics physics) = FreshWorld(cvars);

        var spec = SpectatorObserver();
        spec.SpectatorSpeed = 0f;          // unseeded
        spec.WasSpectatorLastTick = false; // first tick after un-spawning

        clock.Time += Dt;
        physics.Move(spec, ForwardInput(impulse: 10));

        // seeded to the multiplier (2), NOT stepped to 2.5
        Assert.Equal(2f, spec.SpectatorSpeed, 3);
    }

    [Fact]
    public void Spectator_SpeedLadder_SeedsFromCvarRaw_NoFakeDefault()
    {
        // QC: maxspeed_mod = autocvar_sv_spectator_speed_multiplier (no inline default → 0 when unset). The port
        // must read it RAW — NOT substitute the cfg's 1.5. With the cvar unset, the seed is 0.
        (MutableClock clock, PlayerPhysics physics) = FreshWorld(new DictCvars()); // empty store → cvar reads 0

        var spec = SpectatorObserver();
        spec.SpectatorSpeed = 0f;
        spec.WasSpectatorLastTick = false;

        clock.Time += Dt;
        physics.Move(spec, ForwardInput(impulse: 10));

        // unset cvar → seed is 0 (the engine default), not a baked-in 1.5.
        Assert.Equal(0f, spec.SpectatorSpeed, 3);
    }

    [Fact]
    public void Spectator_FliesWithMaxspeedMod()
    {
        // a Noclip observer with SPECTATORSPEED=3 reaches a higher top horizontal speed than a plain Noclip
        // entity (maxspeed_mod=1), because the fly branch's vel_max/accel are scaled by SPECTATORSPEED.
        const int ticks = 40;

        // control: plain (non-observer) Noclip entity → fly branch with maxspeed_mod = 1.
        (MutableClock clockA, PlayerPhysics physA) = FreshWorld();
        var control = new Entity
        {
            Origin = Vector3.Zero,
            Mins = new Vector3(-16f, -16f, -24f),
            Maxs = new Vector3(16f, 16f, 45f),
            Gravity = 1f,
            MoveType = MoveType.Noclip,
            Flags = EntFlags.JumpReleased,
        };
        for (int i = 0; i < ticks; i++) { clockA.Time += Dt; physA.Move(control, ForwardInput()); }
        float controlSpeed = Vec2Len(control.Velocity);

        // spectator: Noclip observer, SPECTATORSPEED = 3 (no impulse → stays 3).
        (MutableClock clockB, PlayerPhysics physB) = FreshWorld();
        var spec = SpectatorObserver();
        spec.SpectatorSpeed = 3f;
        spec.WasSpectatorLastTick = true;
        for (int i = 0; i < ticks; i++) { clockB.Time += Dt; physB.Move(spec, ForwardInput()); }
        float specSpeed = Vec2Len(spec.Velocity);

        Assert.True(specSpeed > controlSpeed + 1f,
            $"spectator (×3) should fly faster than the plain fly entity: spec {specSpeed:F1} vs control {controlSpeed:F1}");
    }

    [Fact]
    public void Spectator_DeadPlayer_NotSpectator()
    {
        // QC's !IS_PLAYER is FALSE for a dead player (classname stays "player"); only a real observer enters the
        // spectator branch. The port maps !IS_PLAYER → Player.IsObserver, so a dead non-observer must NOT touch
        // SPECTATORSPEED even with a speed-step impulse.
        (MutableClock clock, PlayerPhysics physics) = FreshWorld(new DictCvars());

        var dead = new Player
        {
            Origin = new Vector3(0f, 0f, 24.125f),
            Mins = new Vector3(-16f, -16f, -24f),
            Maxs = new Vector3(16f, 16f, 45f),
            Gravity = 1f,
            Flags = EntFlags.OnGround | EntFlags.JumpReleased,
            IsObserver = false,
            DeadState = DeadFlag.Dead,
            SpectatorSpeed = 0f,
        };

        clock.Time += Dt;
        physics.Move(dead, ForwardInput(impulse: 10));

        Assert.Equal(0f, dead.SpectatorSpeed); // untouched — the spectator branch never ran
        Assert.False(dead.WasSpectatorLastTick); // not an observer → postupdate leaves it false
    }

    [Fact]
    public void Spectator_PostUpdate_TracksWasSpectator()
    {
        // The end-of-tick bookkeeping (QC physics.qc:194 lastclassname = classname) must set
        // WasSpectatorLastTick from the CURRENT observer flag so the NEXT tick's ladder gate is right.
        (MutableClock clock, PlayerPhysics physics) = FreshWorld(new DictCvars());

        var spec = SpectatorObserver();
        spec.WasSpectatorLastTick = false;
        clock.Time += Dt;
        physics.Move(spec, ForwardInput());
        Assert.True(spec.WasSpectatorLastTick, "an observer must be flagged as 'was spectator' after its tick");
    }

    // ===========================================================================================
    //  IMovementInput.Impulse contract (net-layer seam is in game/, not test-visible)
    // ===========================================================================================

    [Fact]
    public void MovementInput_CarriesImpulse_AndDefaultsToZero()
    {
        // ServerNet.ToMovementInput copies InputCommand.Impulse → MovementInput.Impulse (a game/ seam not
        // reachable from src/-only tests); here we assert the Common contract those edits rely on: the struct
        // round-trips the field and the interface default is 0.
        IMovementInput zero = new MovementInput { FrameTime = Dt };
        Assert.Equal(0, zero.Impulse);

        IMovementInput withImpulse = new MovementInput { FrameTime = Dt, Impulse = 10 };
        Assert.Equal(10, withImpulse.Impulse);
    }

    // ===========================================================================================
    //  helpers
    // ===========================================================================================

    private static Entity GroundPlayer() => new()
    {
        Origin = new Vector3(0f, 0f, 24.125f),
        Velocity = Vector3.Zero,
        Mins = new Vector3(-16f, -16f, -24f),
        Maxs = new Vector3(16f, 16f, 45f),
        Gravity = 1f,
        Flags = EntFlags.OnGround | EntFlags.JumpReleased,
    };

    private static Player SpectatorObserver() => new()
    {
        Origin = Vector3.Zero,
        Velocity = Vector3.Zero,
        Mins = new Vector3(-16f, -16f, -24f),
        Maxs = new Vector3(16f, 16f, 45f),
        Gravity = 1f,
        MoveType = MoveType.Noclip, // observers TRANSMUTE to NOCLIP/FLY → take the fly branch
        Flags = EntFlags.JumpReleased,
        IsObserver = true,
    };

    private static float Vec2Len(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);

    private static void AssertClose(Vector3 expected, Vector3 actual, string msg)
    {
        Assert.True((expected - actual).Length() < 1e-3f, $"{msg}: expected {expected}, got {actual}");
    }

    /// <summary>A dictionary-backed settable cvar store (the parity harness's EmptyCvars can't be set).</summary>
    private sealed class DictCvars : ICvarService
    {
        private readonly Dictionary<string, string> _v = new();
        public float GetFloat(string name) => _v.TryGetValue(name, out string? s) && float.TryParse(s,
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0f;
        public string GetString(string name) => _v.TryGetValue(name, out string? s) ? s : "";
        public void Set(string name, string value) => _v[name] = value;
        public void Register(string name, string defaultValue, CvarFlags flags = CvarFlags.None) => _v.TryAdd(name, defaultValue);
    }

    /// <summary>Like <see cref="MovementTestServices"/> but with a caller-supplied (settable) cvar store.</summary>
    private sealed class SpectatorTestServices : IEngineServices
    {
        public ITraceService Trace { get; }
        public IGameClock Clock { get; }
        public ICvarService Cvars { get; }
        public IEntityService Entities { get; } = new Ent();
        public ISoundService Sound { get; } = new Snd();
        public IModelService Models { get; } = new Mdl();

        public SpectatorTestServices(AnalyticWorld world, MutableClock clock, ICvarService cvars)
        {
            Trace = new AnalyticTraceService(world);
            Clock = clock;
            Cvars = cvars;
        }

        private sealed class Ent : IEntityService
        {
            public Entity Spawn() => new();
            public void Remove(Entity e) { }
            public void SetOrigin(Entity e, Vector3 origin) => e.Origin = origin;
            public void SetSize(Entity e, Vector3 mins, Vector3 maxs) { e.Mins = mins; e.Maxs = maxs; }
            public void SetModel(Entity e, string model) { }
            public IEnumerable<Entity> FindByClass(string className) => System.Array.Empty<Entity>();
            public IEnumerable<Entity> FindInRadius(Vector3 origin, float radius) => System.Array.Empty<Entity>();
        }
        private sealed class Snd : ISoundService
        {
            public void Play(Entity e, SoundChannel channel, string sample, float volume = 1f, float attenuation = 1f, bool loop = false, float pitch = 1f) { }
            public void Stop(Entity e, SoundChannel channel) { }
        }
        private sealed class Mdl : IModelService
        {
            public bool TryGetTag(Entity e, string tagName, out Vector3 origin, out Vector3 forward, out Vector3 right, out Vector3 up)
            { origin = forward = right = up = Vector3.Zero; return false; }
            public void SetAttachment(Entity e, Entity parent, string tagName) { }
        }
    }
}
