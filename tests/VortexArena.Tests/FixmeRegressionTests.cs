using System.Numerics;
using XonoticGodot.Common;
using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Physics;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Regression coverage for the FIXME bug pass (see planning/FIXME.md):
///   F02 — a DEAD player must not move under WASD (the shared PlayerPhysics.Move dead-gate).
///   F04 — a CTF flag must get a MODEL on spawn (else it networks invisible) and must RIDE its carrier.
/// F01 (remote-anim wire) and F03 (respawn view snap) live in the game/ net layer (not visible to this test
/// assembly); they're covered by the build + the existing LocomotionBlend tests for the selection logic.
/// </summary>
[Collection("GlobalState")]
public class FixmeRegressionTests
{
    // ---- F02: dead players don't move -------------------------------------------------------------

    private static (Entity player, PlayerPhysics physics) BuildOnFloor()
    {
        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(-4096f, -4096f, -64f), new Vector3(4096f, 4096f, 0f), SuperContents.Solid));
        world.BuildGrid();
        GameInit.Boot(new EngineServices(world));

        Entity p = Api.Entities.Spawn();
        p.ClassName = "player";
        p.MoveType = MoveType.Walk;
        p.Solid = Solid.SlideBox;
        p.Flags |= EntFlags.Client | EntFlags.JumpReleased;
        p.Mins = new Vector3(-16f, -16f, -24f);
        p.Maxs = new Vector3(16f, 16f, 45f);
        p.ViewOfs = new Vector3(0f, 0f, 35f);
        p.Gravity = 1f;
        p.Health = 100f;
        p.Origin = new Vector3(0f, 0f, 24f);
        Api.Entities.SetOrigin(p, p.Origin);
        return (p, new PlayerPhysics());
    }

    private static MovementInput Forward() => new()
    {
        FrameTime = 1f / 72f,
        MoveValues = new Vector3(400f, 0f, 0f), // full forward wish-move
        ViewAngles = Vector3.Zero,
    };

    [Fact]
    public void AlivePlayer_MovesUnderForwardInput()
    {
        var (p, phys) = BuildOnFloor();
        Vector3 before = p.Origin;
        for (int i = 0; i < 20; i++) phys.Move(p, Forward());
        Assert.True((p.Origin - before).Length() > 1f, "an alive player should advance under forward input");
    }

    [Fact]
    public void DeadPlayer_DoesNotMoveUnderForwardInput()
    {
        var (p, phys) = BuildOnFloor();
        p.DeadState = DeadFlag.Dead; // QC IS_DEAD: PlayerThink bails before PM_Main
        Vector3 before = p.Origin;
        for (int i = 0; i < 20; i++) phys.Move(p, Forward());
        Assert.Equal(before, p.Origin); // the corpse must not slide / steer
    }

    [Fact]
    public void DeadPlayer_VelocityIsZeroed_SoTheDeathCamDoesNotShake()
    {
        // Dying mid-fall over a void: the body carries a large downward velocity. PlayerPhysics.Move must clear
        // it (the corpse is frozen) — otherwise NetGame extrapolates the eye by it and the death-cam shakes.
        var (p, phys) = BuildOnFloor();
        p.DeadState = DeadFlag.Dead;
        p.Velocity = new Vector3(120f, 0f, -800f); // free-fall + drift at the moment of death
        phys.Move(p, Forward());
        Assert.Equal(Vector3.Zero, p.Velocity);
    }

    // ---- F04: flags render + ride the carrier -----------------------------------------------------

    private static Ctf BuildCtf()
    {
        Api.Services = new EngineServices(new CollisionWorld());
        return new Ctf();
    }

    private static Player NewPlayer(int team) => new()
    {
        Team = team,
        Health = 100f,
        Flags = EntFlags.Client,
    };

    [Fact]
    public void SpawnFlag_GivesTheFlagAModel_SoItRenders()
    {
        var ctf = BuildCtf();
        FlagState red = ctf.SpawnFlag(Teams.Red, new Vector3(100f, 0f, 0f));
        Assert.NotNull(red.Entity);
        Assert.False(string.IsNullOrEmpty(red.Entity!.Model)); // the flag was networking invisible before the fix
    }

    [Fact]
    public void CarriedFlag_RidesTheCarrier_OnTick()
    {
        var ctf = BuildCtf();
        FlagState blue = ctf.SpawnFlag(Teams.Blue, new Vector3(-100f, 0f, 0f));
        ctf.SpawnFlag(Teams.Red, new Vector3(100f, 0f, 0f));
        ctf.Activate();

        var redPlayer = NewPlayer(Teams.Red);
        redPlayer.Origin = new Vector3(-100f, 0f, 0f);
        Assert.True(ctf.Pickup(redPlayer, blue));        // red carries the blue flag

        redPlayer.Origin = new Vector3(500f, 0f, 200f);  // carrier runs off
        ctf.Tick();                                       // QC setattachment follow

        Assert.NotNull(blue.Entity);
        Assert.True((blue.Entity!.Origin - redPlayer.Origin).Length() < 64f,
            "a carried flag must track its carrier, not stay at the pickup spot");
    }
}
