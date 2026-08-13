using VortexArena.Engine.Console;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// DP kbutton split for the movement keys (cl_input.c): <c>+moveleft</c>/<c>+moveright</c> STRAFE,
/// <c>+left</c>/<c>+right</c> TURN the view at <c>cl_yawspeed</c>, and <c>+strafe</c> reroutes the turn keys
/// onto sidemove. The port previously aliased <c>+left</c>/<c>+right</c> straight onto strafe, so the classic
/// Quake turn binds silently strafed and <c>cl_yawspeed</c> did not exist. These lock the split in.
///
/// <para>The turn keys also carry the cross-engine strafe capture (docs/MOVEMENT-PHYSICS.md §10): a scripted
/// constant-rate turn is the only strafe input a stock Xonotic client can reproduce exactly, so if these
/// regress, that comparison silently stops being the same experiment on both sides.</para>
/// </summary>
public class KeyboardTurnBindTests
{
    private static void Press(string key, string command, bool pressed = true)
    {
        BindTable.Bind(key, command);
        BindTable.HandleBind(key, pressed, _ => { });
    }

    [Fact]
    public void MoveLeftRight_Strafe_AndDoNotTurn()
    {
        BindTable.Reset();
        Press("A", "+moveleft");
        Assert.Equal(-1f, BindTable.Side);
        Assert.Equal(0f, BindTable.TurnLeftAxis);

        BindTable.Reset();
        Press("D", "+moveright");
        Assert.Equal(1f, BindTable.Side);
        Assert.Equal(0f, BindTable.TurnLeftAxis);
    }

    [Fact]
    public void LeftRight_Turn_AndDoNotStrafe()
    {
        // cl_input.c:441-445 — +left increases yaw (turns left), +right decreases it.
        BindTable.Reset();
        Press("LEFTARROW", "+left");
        Assert.Equal(1f, BindTable.TurnLeftAxis);
        Assert.Equal(0f, BindTable.Side);

        BindTable.Reset();
        Press("RIGHTARROW", "+right");
        Assert.Equal(-1f, BindTable.TurnLeftAxis);
        Assert.Equal(0f, BindTable.Side);
    }

    [Fact]
    public void StrafeModifier_ReroutesTurnKeysToSidemove()
    {
        // cl_input.c:500-504 — while +strafe is held, +left/+right feed sidemove and stop turning.
        BindTable.Reset();
        Press("ALT", "+strafe");
        Press("RIGHTARROW", "+right");
        Assert.Equal(1f, BindTable.Side);
        Assert.Equal(0f, BindTable.TurnLeftAxis);

        // Releasing +strafe hands the same key back to turning.
        BindTable.HandleBind("ALT", false, _ => { });
        Assert.Equal(0f, BindTable.Side);
        Assert.Equal(-1f, BindTable.TurnLeftAxis);
    }

    [Fact]
    public void StrafeModifier_CombinedWithMoveKey_StaysClamped()
    {
        // +moveright and +right together under +strafe would sum to 2 without the clamp, which would scale
        // the wish-move past the ±1 the sampler promises WishMoveScaling.
        BindTable.Reset();
        Press("ALT", "+strafe");
        Press("D", "+moveright");
        Press("RIGHTARROW", "+right");
        Assert.Equal(1f, BindTable.Side);
    }

    [Fact]
    public void SpeedModifier_AndLookKeys_Latch()
    {
        BindTable.Reset();
        Press("SHIFT", "+speed");
        Assert.True(BindTable.SpeedMod);

        Press("PGUP", "+lookup");
        Assert.Equal(1f, BindTable.LookUpAxis);
        Press("PGDN", "+lookdown");
        Assert.Equal(0f, BindTable.LookUpAxis); // both held cancel, like DP's up/down key states

        BindTable.HandleBind("PGUP", false, _ => { });
        Assert.Equal(-1f, BindTable.LookUpAxis);
    }

    [Fact]
    public void ReleaseAll_ClearsTurnAndModifierState()
    {
        // DP in_releaseall: opening the console must not leave a turn key latched, or the view spins on its own.
        BindTable.Reset();
        Press("LEFTARROW", "+left");
        Press("SHIFT", "+speed");
        Press("ALT", "+strafe");

        BindTable.ReleaseAll();
        Assert.Equal(0f, BindTable.TurnLeftAxis);
        Assert.Equal(0f, BindTable.LookUpAxis);
        Assert.False(BindTable.SpeedMod);
        Assert.False(BindTable.StrafeMod);
    }
}
