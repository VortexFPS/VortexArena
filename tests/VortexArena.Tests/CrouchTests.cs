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
/// Crouch / duck parity with QC <c>PM_ClientMovement_UpdateStatus</c> (common/physics/player.qc): holding the
/// crouch button shrinks the hull (<c>setsize</c> PL_CROUCH_MIN/MAX), drops the eye (<c>view_ofs</c> =
/// STAT(PL_CROUCH_VIEW_OFS) — '0 0 20' vs the standing '0 0 35'), and on release stands up ONLY if the standing
/// hull isn't blocked overhead (the <c>tracebox(... PL_MIN, PL_MAX ...); if (!trace_startsolid)</c> guard).
///
/// The hull + 0.5x speed already worked; the missing half (and the felt "crouch doesn't work" bug) was that the
/// eye never dropped — <see cref="PlayerPhysics.UpdateCrouch"/> didn't touch <see cref="Entity.ViewOfs"/> and the
/// render camera used a fixed eye height. These tests assert the full transition: hull, eye, and the un-crouch
/// obstruction latch.
/// </summary>
public class CrouchTests
{
    // QC defaults: sv_player_maxs '16 16 45' / viewoffset '0 0 35'; sv_player_crouch_maxs '16 16 25' / '0 0 20'.
    private const float StandTop = 45f, CrouchTop = 25f, StandEye = 35f, CrouchEye = 20f;

    private sealed class World
    {
        public Entity Player = null!;
        public PlayerPhysics Physics = null!;
    }

    /// <summary>Boot an engine facade over a flat floor (top at Z=0), optionally with a low ceiling, and stand a
    /// player on it at origin Z=24 (feet at 0). <paramref name="ceilingBottomZ"/> &lt; 0 = no ceiling.</summary>
    private static World Build(float ceilingBottomZ = -1f)
    {
        // Hermetic: UpdateCrouch consults the PlayerCanCrouch mutator hook; clear any handler a sibling test left.
        MutatorHooks.PlayerCanCrouch.Clear();

        var world = new CollisionWorld();
        world.AddBrush(Brush.FromBox(new Vector3(-4096f, -4096f, -64f), new Vector3(4096f, 4096f, 0f), SuperContents.Solid));
        if (ceilingBottomZ >= 0f)
            world.AddBrush(Brush.FromBox(new Vector3(-4096f, -4096f, ceilingBottomZ), new Vector3(4096f, 4096f, ceilingBottomZ + 64f), SuperContents.Solid));
        world.BuildGrid();

        var services = new EngineServices(world);
        GameInit.Boot(services); // Api.Services / Api.Trace = this world

        Entity player = Api.Entities.Spawn();
        player.ClassName = "player";
        player.MoveType = MoveType.Walk;
        player.Solid = Solid.SlideBox;
        player.Flags |= EntFlags.Client | EntFlags.JumpReleased;
        player.Mins = new Vector3(-16f, -16f, -24f);
        player.Maxs = new Vector3(16f, 16f, StandTop);
        player.ViewOfs = new Vector3(0f, 0f, StandEye);
        player.Gravity = 1f;
        player.Health = 100f;
        player.Origin = new Vector3(0f, 0f, 24f); // feet at floor top (0)
        Api.Entities.SetOrigin(player, player.Origin);

        return new World { Player = player, Physics = new PlayerPhysics() };
    }

    private static MovementInput Step(bool crouch) => new()
    {
        FrameTime = 1f / 72f,
        ButtonCrouch = crouch,
    };

    [Fact]
    public void Holding_Crouch_Shrinks_Hull_And_Drops_The_Eye()
    {
        World w = Build();

        w.Physics.Move(w.Player, Step(crouch: true));

        Assert.True(w.Player.IsDucked);
        Assert.Equal(CrouchTop, w.Player.Maxs.Z, 3);
        Assert.Equal(CrouchEye, w.Player.ViewOfs.Z, 3); // the eye dropped 35 -> 20 (the bug this fixes)
    }

    [Fact]
    public void Releasing_Crouch_In_The_Open_Stands_Back_Up()
    {
        World w = Build();

        w.Physics.Move(w.Player, Step(crouch: true));
        Assert.True(w.Player.IsDucked);

        w.Physics.Move(w.Player, Step(crouch: false));

        Assert.False(w.Player.IsDucked);
        Assert.Equal(StandTop, w.Player.Maxs.Z, 3);
        Assert.Equal(StandEye, w.Player.ViewOfs.Z, 3); // eye restored to standing
    }

    [Fact]
    public void Releasing_Crouch_Under_A_Low_Ceiling_Stays_Ducked()
    {
        // Ceiling bottom at Z=55: the standing head (origin 24 + maxs 45 = 69) hits it, the crouched head
        // (24 + 25 = 49) fits. QC: the un-crouch tracebox is startsolid, so the player can't stand up.
        World w = Build(ceilingBottomZ: 55f);

        w.Physics.Move(w.Player, Step(crouch: true));
        Assert.True(w.Player.IsDucked);

        w.Physics.Move(w.Player, Step(crouch: false)); // wants to stand, but blocked overhead

        Assert.True(w.Player.IsDucked);                 // latched crouched
        Assert.Equal(CrouchTop, w.Player.Maxs.Z, 3);
        Assert.Equal(CrouchEye, w.Player.ViewOfs.Z, 3); // eye stays low while stuck
    }
}
