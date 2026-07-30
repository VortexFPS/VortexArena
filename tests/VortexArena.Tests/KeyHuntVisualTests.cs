using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for §4.10: the KeyHunt key gets a model + team color + glow + per-team netname + spin so the networked
/// entity stream renders it (QC kh_Key_Spawn), and a carried key stops spinning (rides the carrier).
/// </summary>
[Collection("GlobalState")]
public class KeyHuntVisualTests
{
    public KeyHuntVisualTests() => Api.Services = new EngineServices(new CollisionWorld());

    [Fact]
    public void SpawnKey_GivesModelColorAndSpin()
    {
        var kh = new KeyHunt();
        var owner = new Player { Team = Teams.Red, Flags = EntFlags.Client };
        KeyState key = kh.SpawnKey(Teams.Red, owner);

        Assert.NotNull(key.Entity);
        Entity e = key.Entity!;
        Assert.False(string.IsNullOrEmpty(e.Model));          // has a model → networked + rendered
        Assert.Contains("key", e.Model);
        Assert.Equal(Teams.Red, (int)e.Team);                 // colormap = team
        Assert.Equal("^1red key", e.NetName);                 // per-team colored name
        Assert.True((e.Effects & EffectFlags.FullBright) != 0); // glow
        Assert.NotEqual(System.Numerics.Vector3.Zero, e.AVelocity); // a loose key spins
    }

    [Fact]
    public void CarriedKey_StopsSpinning()
    {
        var kh = new KeyHunt();
        var owner = new Player { Team = Teams.Blue, Flags = EntFlags.Client };
        KeyState key = kh.SpawnKey(Teams.Blue, owner);
        Assert.NotEqual(System.Numerics.Vector3.Zero, key.Entity!.AVelocity);

        kh.AssignKeyNoScore(owner, key); // attach to the carrier
        Assert.Equal(System.Numerics.Vector3.Zero, key.Entity!.AVelocity); // carried → no spin
        Assert.Same(owner, key.Carrier);
    }
}
