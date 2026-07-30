using System.Numerics;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

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
