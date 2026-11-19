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
/// Tests for T18 CTF DEPTH: flag passing + throwing (g_ctf_pass / g_ctf_throw, QC ctf_Handle_Throw +
/// ctf_CalculatePassVelocity + the FLAG_PASSING in-flight think). The pass flies to a teammate and is
/// retrieved; the throw drops the flag with a forward velocity; the in-flight pass gives up on timeout.
/// </summary>
[Collection("GlobalState")]
public class CtfPassThrowTests
{
    private static EngineServices Facade()
    {
        var es = new EngineServices(new CollisionWorld());
        Api.Services = es;
        return es;
    }

    private static void SetTime(EngineServices es, float t) =>
        typeof(GameClock).GetProperty("Time")!.SetValue(es.ClockImpl, t);

    private static Player NewPlayer(int team, Vector3 origin = default) =>
        new Player { Team = team, Flags = EntFlags.Client, Origin = origin };

    private static Ctf TwoBase(out FlagState red, out FlagState blue)
    {
        var ctf = new Ctf();
        red = ctf.SpawnFlag(Teams.Red, new Vector3(100, 0, 0));
        blue = ctf.SpawnFlag(Teams.Blue, new Vector3(-100, 0, 0));
        ctf.Activate();
        return ctf;
    }

    [Theory]
    [InlineData("0", FlagStatus.Dropped)]
    [InlineData("1", FlagStatus.Carried)]
    public void PortalTeleport_DropsOnlyWhenConfigured(string setting, FlagStatus expected)
    {
        Facade();
        Api.Cvars.Set("g_ctf_portalteleport", setting);
        Ctf ctf = TwoBase(out _, out FlagState blue);
        try
        {
            var carrier = NewPlayer(Teams.Red);
            Assert.True(ctf.Pickup(carrier, blue));

            typeof(WarpzoneManager).GetMethod("NotifyPortalTeleport",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, new object[] { carrier });

            Assert.Equal(expected, blue.Status);
        }
        finally
        {
            ctf.Deactivate();
        }
    }

    [Theory]
    [InlineData("0", 200f, 0f)]
    [InlineData("1", 240f, 150f)]
    [InlineData("2", 172.28757f, 0f)]
    public void FlagLaunch_UsesConfiguredProjectileStyleAndSpeed(string style, float x, float y)
    {
        Facade();
        Api.Cvars.Set("g_projectiles_newton_style", style);
        Api.Cvars.Set("g_weaponspeedfactor", "2");
        Api.Cvars.Set("g_ctf_throw_velocity_forward", "100");
        Api.Cvars.Set("g_ctf_throw_velocity_up", "0");
        Ctf ctf = TwoBase(out _, out FlagState blue);
        try
        {
            var carrier = NewPlayer(Teams.Red);
            carrier.Velocity = new Vector3(40f, 150f, 0f);
            Assert.True(ctf.Pickup(carrier, blue));
            Assert.True(ctf.ThrowFlag(carrier));
            Assert.True(Vector3.Distance(new Vector3(x, y, 0f), blue.Entity!.Velocity) < 0.001f);
        }
        finally { ctf.Deactivate(); }
    }

    [Fact]
    public void CarrierDrop_UsesAbsoluteVelocityAtStockSettings()
    {
        Facade();
        Api.Cvars.Set("g_ctf_drop_velocity_side", "0");
        Api.Cvars.Set("g_ctf_drop_velocity_up", "200");
        Ctf ctf = TwoBase(out _, out FlagState blue);
        try
        {
            var carrier = NewPlayer(Teams.Red);
            carrier.Velocity = new Vector3(350f, 100f, 50f);
            Assert.True(ctf.Pickup(carrier, blue));
            ctf.DropFlag(carrier);
            Assert.Equal(new Vector3(0f, 0f, 200f), blue.Entity!.Velocity);
        }
        finally { ctf.Deactivate(); }
    }

    [Fact]
    public void DropSpecialItems_ThrowsEvenWhenManualThrowsAreDisabled()
    {
        Facade();
        Api.Cvars.Set("g_ctf_throw", "0");
        Ctf ctf = TwoBase(out _, out FlagState blue);
        try
        {
            var carrier = NewPlayer(Teams.Red);
            Assert.True(ctf.Pickup(carrier, blue));
            Assert.False(ctf.ThrowFlag(carrier));

            MutatorHooks.FireDropSpecialItems(carrier);

            Assert.Equal(FlagStatus.Dropped, blue.Status);
            Assert.True(blue.Entity!.Velocity.X > 0f);
        }
        finally
        {
            ctf.Deactivate();
        }
    }

    [Theory]
    [InlineData("1", DeathTypes.Generic, "0", 0, FlagStatus.AtBase)]
    [InlineData("0", DeathTypes.Generic, "0", 0, FlagStatus.Dropped)]
    [InlineData("0", DeathTypes.Lava, "0", 0, FlagStatus.AtBase)]
    [InlineData("0", DeathTypes.Void, "0.4", 1, FlagStatus.Dropped)]
    [InlineData("0", DeathTypes.Slime, "0.4", 2, FlagStatus.AtBase)]
    public void DroppedFlag_ReturnsOnConfiguredDamage(string returnDamage, string deathType,
        string delay, int thinkCount, FlagStatus expected)
    {
        var engine = Facade();
        Api.Cvars.Set("g_ctf_flag_return_damage", returnDamage);
        Api.Cvars.Set("g_ctf_flag_health", "10");
        Api.Cvars.Set("g_ctf_flag_return_damage_delay", delay);
        Api.Cvars.Set("g_ctf_flag_return_dropped", "0");
        Ctf ctf = TwoBase(out _, out FlagState blue);
        try
        {
            var carrier = NewPlayer(Teams.Red);
            Assert.True(ctf.Pickup(carrier, blue));
            ctf.DropFlag(carrier);
            Entity entity = blue.Entity!;
            Assert.Equal(returnDamage == "1" ? 10f : 100f, entity.Health);

            new DamageSystem().Apply(new DamageInfo
            {
                Target = entity,
                Amount = 10f,
                DeathType = deathType,
                HitLocation = entity.Origin,
            });
            for (int i = 0; i < thinkCount; i++)
            {
                SetTime(engine, (i + 1) * 0.2f);
                entity.Think!(entity);
            }

            Assert.Equal(expected, blue.Status);
        }
        finally
        {
            ctf.Deactivate();
        }
    }

    /// <summary>
    /// A carrier passing to a teammate puts the flag into FLAG_PASSING with a velocity aimed at the receiver;
    /// driving the in-flight think while the flag is at the receiver retrieves it (QC ctf_Handle_Retrieve).
    /// </summary>
    [Fact]
    public void Pass_TransfersFlagToReceiver()
    {
        var es = Facade();
        Ctf ctf = TwoBase(out _, out FlagState blue);

        var carrier = NewPlayer(Teams.Red, new Vector3(0, 0, 0));
        var mate = NewPlayer(Teams.Red, new Vector3(40, 0, 0));

        // carrier picks up the blue flag, then passes to the nearby teammate.
        Assert.True(ctf.Pickup(carrier, blue));
        Assert.Same(blue, ctf.CarriedBy(carrier));

        Assert.True(ctf.PassFlag(carrier, mate));
        Assert.Equal(FlagStatus.Passing, blue.Status);
        Assert.Same(mate, blue.PassTarget);
        Assert.True(blue.Entity!.Velocity.Length() > 0f); // flying toward the receiver

        // The flag entity is within the receiver's catch range → the in-flight think retrieves it.
        ctf.Tick();
        Assert.Same(blue, ctf.CarriedBy(mate));
        Assert.Equal(FlagStatus.Carried, blue.Status);
        Assert.Null(ctf.CarriedBy(carrier));
    }

    /// <summary>
    /// A pass whose target wanders out of range / never arrives gives up after g_ctf_pass_timelimit and becomes
    /// a normal dropped flag (QC the FLAG_PASSING give-up → ctf_Handle_Drop DROP_PASS).
    /// </summary>
    [Fact]
    public void Pass_TimesOut_BecomesDropped()
    {
        var es = Facade();
        Ctf ctf = TwoBase(out _, out FlagState blue);

        var carrier = NewPlayer(Teams.Red, new Vector3(0, 0, 0));
        // Receiver far enough that the catch never triggers (just inside the pass radius at launch).
        var mate = NewPlayer(Teams.Red, new Vector3(300, 0, 0));

        Assert.True(ctf.Pickup(carrier, blue));
        Assert.True(ctf.PassFlag(carrier, mate));
        Assert.Equal(FlagStatus.Passing, blue.Status);

        // Move the receiver out of range AND advance past the pass time limit → give up.
        mate.Origin = new Vector3(5000, 0, 0);
        SetTime(es, ctf.PassTimelimit + 1f);
        ctf.Tick();

        Assert.Equal(FlagStatus.Dropped, blue.Status); // pass failed → dropped
        Assert.Null(ctf.CarriedBy(mate));
        Assert.Null(blue.PassTarget);
    }

    /// <summary>
    /// Throwing the flag (DROP_THROW) drops it with a forward+up velocity and frees the carrier (QC the
    /// DROP_THROW case of ctf_Handle_Throw).
    /// </summary>
    [Fact]
    public void Throw_DropsFlagWithVelocity()
    {
        var es = Facade();
        Ctf ctf = TwoBase(out _, out FlagState blue);

        var carrier = NewPlayer(Teams.Red, new Vector3(0, 0, 0));
        carrier.Angles = new Vector3(0, 0, 0); // facing +X (yaw 0)
        Assert.True(ctf.Pickup(carrier, blue));

        Assert.True(ctf.ThrowFlag(carrier));
        Assert.Equal(FlagStatus.Dropped, blue.Status);
        Assert.Null(ctf.CarriedBy(carrier));
        // forward velocity (>0 horizontally) + upward leg (QC g_ctf_throw_velocity_up).
        Vector3 v = blue.Entity!.Velocity;
        Assert.True(new Vector2(v.X, v.Y).Length() > 0f);
        Assert.True(v.Z > 0f);
    }

    /// <summary>
    /// A teammate's +use pass request pulls the flag from the nearest in-radius carrier to the requester
    /// (QC g_ctf_pass_request → ctf_Handle_Throw DROP_PASS to the requester).
    /// </summary>
    [Fact]
    public void RequestPass_PullsFromNearestCarrier()
    {
        var es = Facade();
        Ctf ctf = TwoBase(out _, out FlagState blue);

        var carrier = NewPlayer(Teams.Red, new Vector3(0, 0, 0));
        var requester = NewPlayer(Teams.Red, new Vector3(30, 0, 0));
        Assert.True(ctf.Pickup(carrier, blue));

        var roster = new[] { carrier, requester };
        Assert.True(ctf.RequestPass(requester, roster));
        Assert.Equal(FlagStatus.Passing, blue.Status);
        Assert.Same(requester, blue.PassTarget); // the pass flies to the requester

        // it is in catch range → the next think delivers it.
        ctf.Tick();
        Assert.Same(blue, ctf.CarriedBy(requester));
    }
}
