using System.Collections.Generic;
using System.Numerics;
using VortexArena.Common.Framework;
using VortexArena.Common.Gameplay;
using VortexArena.Common.Services;
using VortexArena.Engine.Collision;
using VortexArena.Engine.Simulation;
using VortexArena.Server;
using Xunit;
using Xunit.Abstractions;

namespace VortexArena.Tests;

/// <summary>
/// <c>sv_maxvelocity</c> — SV_CheckVelocity's universal speed limit — must come from the CVAR, not from the
/// DarkPlaces engine default.
///
/// The bug ("the blaster projectile feels slow"): <c>MoveTypePhysics.MaxVelocity</c> was
/// <c>const float = 2000f</c>, DP's <c>sv_maxvelocity</c> engine default (sv_main.c:143). Xonotic overrides it
/// in <c>xonotic-server.cfg:325</c> with <c>sv_maxvelocity 1000000000</c>, i.e. the shipped game has no clamp at
/// all. Every projectile balanced above 2000 qu/s was therefore silently slowed: the Blaster bolt
/// (<c>g_balance_blaster_primary_speed</c> = 6000) flew at exactly ONE THIRD of its intended speed, and the
/// HLAC (6000), Seeker tag (5000), Crylink secondary / Seeker flac (3000), Electro primary / OK RPC (2500),
/// Arc bolt (2300) and Hagar (2200) bolts were clipped too — as was any knockback impulse over 2000.
///
/// Mutates Api.Services and the MoveTypePhysics static, so it runs in the serialized GlobalState collection.
/// </summary>
[Collection("GlobalState")]
public sealed class MaxVelocityClampTests
{
    private readonly ITestOutputHelper _out;
    public MaxVelocityClampTests(ITestOutputHelper output) => _out = output;

    private static CollisionWorld OpenSpace()
    {
        var w = new CollisionWorld();
        w.AddBrush(Brush.FromBox(new Vector3(-16384f, -16384f, -128f), new Vector3(16384f, 16384f, -64f), SuperContents.Solid));
        w.BuildGrid();
        return w;
    }

    private static GameWorld BootWorld()
    {
        var world = new GameWorld(OpenSpace(), new List<EntityDict>
        {
            new("worldspawn"),
            new("info_player_deathmatch", new Vector3(0f, 0f, 32f)),
        });
        world.Boot("dm");
        return world;
    }

    /// <summary>Spawn a bare MOVETYPE_FLY point body at <paramref name="speed"/> along +X, tick one server
    /// frame, and report the speed that survived SV_CheckVelocity.</summary>
    private static float SpeedAfterOneTick(GameWorld world, float speed)
    {
        Entity m = Api.Entities.Spawn();
        m.ClassName = "blasterbolt";
        Api.Entities.SetOrigin(m, new Vector3(0f, 0f, 256f));
        Api.Entities.SetSize(m, Vector3.Zero, Vector3.Zero);
        m.MoveType = MoveType.Fly;
        m.Solid = Solid.Trigger;
        m.Velocity = new Vector3(speed, 0f, 0f);
        world.Frame(SimulationLoop.TicRate);
        return m.Velocity.Length();
    }

    /// <summary>With no cvar store to read (bare libraries / headless tests), the default must be Xonotic's
    /// shipped value, not DP's 2000 — so a test path behaves like the real game.</summary>
    [Fact]
    public void DefaultMatchesXonoticsShippedCvarNotTheEngineDefault()
    {
        Assert.True(MoveTypePhysics.MaxVelocity >= 1e9f,
            $"expected the xonotic-server.cfg value (1000000000), got {MoveTypePhysics.MaxVelocity}");
    }

    /// <summary>A Blaster bolt at its balance speed must still be doing 6000 qu/s after a physics tick.</summary>
    [Fact]
    public void BlasterBoltKeepsItsFullBalanceSpeedThroughAPhysicsTick()
    {
        GameWorld world = BootWorld();

        Weapon? blaster = Weapons.ByName("blaster");
        Assert.NotNull(blaster);
        blaster!.Configure();
        Assert.Equal(6000f, ((Blaster)blaster).Primary.Speed);

        float speed = SpeedAfterOneTick(world, 6000f);
        _out.WriteLine($"maxvelocity={MoveTypePhysics.MaxVelocity} bolt speed after one tick = {speed}");
        Assert.Equal(6000f, speed, 1);
    }

    /// <summary>The seam is live: a server that deliberately restores DP's 2000 still gets the clamp (and this
    /// reproduces the old behaviour — a 6000 bolt cut to 2000, the 3x slowdown that was being felt).</summary>
    [Fact]
    public void ExplicitCvarStillClamps()
    {
        GameWorld world = BootWorld();
        float saved = MoveTypePhysics.MaxVelocity;
        try
        {
            world.Services.Cvars.Set("sv_maxvelocity", "2000");
            MoveTypePhysics.ApplyServerCvars(world.Services.Cvars);
            Assert.Equal(2000f, MoveTypePhysics.MaxVelocity);
            Assert.Equal(2000f, SpeedAfterOneTick(world, 6000f), 1);
        }
        finally
        {
            MoveTypePhysics.MaxVelocity = saved;
        }
    }
}
