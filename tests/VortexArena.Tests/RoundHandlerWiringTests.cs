using XonoticGodot.Common.Gameplay;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Server;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// F2 regression guard for the round-handler double-wiring fix (GameWorld.ActivateGameType). A bare
/// <c>EnableRounds()</c> spun up a SECOND, generic round handler whose default predicates did not match the mode:
/// Invasion's solo co-op round never started (DefaultCanRoundStart needs >=2 players), so its pre-round fire gate
/// blocked weapon fire the whole match, and DefaultCanRoundEnd (a degenerate team scan for FFA) could spuriously
/// ResetMap. The self-round-managing modes must therefore create NO generic handler:
///   - Invasion (FFA co-op) self-drives its own inv.Handler via inv.Tick.
///   - KeyHunt self-manages via its own kh_controller RoundPhase machine.
/// Onslaught DOES need the live handler (for the map-reset / countdown / fire-gate that only EnableRounds installs),
/// driven off its own generator predicates. ClanArena is the control: it legitimately keeps the generic handler.
/// </summary>
[Collection("GlobalState")]
public sealed class RoundHandlerWiringTests
{
    private static GameWorld Boot(string gt)
    {
        var world = new GameWorld(new CollisionWorld());
        world.Boot(gt);
        return world;
    }

    [Theory]
    [InlineData("inv")]  // FFA co-op — self-drives inv.Handler; a solo round still starts, so fire is never gated
    [InlineData("kh")]   // team — self-manages via its own RoundPhase machine
    public void SelfManagedMode_HasNoGenericHandler_AndNeverGatesFire(string gt)
    {
        GameWorld world = Boot(gt);
        Assert.Null(world.Rounds);                            // no second, generic round handler
        Assert.Null(WeaponFireDriver.RoundFireForbidden);     // fire is never pre-round-gated (the Invasion fire-block)
    }

    [Fact]
    public void Invasion_RoundVariant_OwnsItsHandler()
    {
        var inv = Assert.IsType<Invasion>(Boot("inv").GameType);
        Assert.NotNull(inv.Handler);                          // the round variant self-drives its own handler
    }

    [Fact]
    public void Onslaught_UsesLiveHandlerAndFireGate()
    {
        GameWorld world = Boot("ons");
        Assert.IsType<Onslaught>(world.GameType);
        Assert.NotNull(world.Rounds);                         // Onslaught needs the live handler (reset / countdown)
        Assert.NotNull(WeaponFireDriver.RoundFireForbidden);  // ... which installs the pre-round fire gate
    }

    [Fact]
    public void ClanArena_Control_KeepsGenericHandler()
    {
        GameWorld world = Boot("ca");
        Assert.IsType<ClanArena>(world.GameType);
        Assert.NotNull(world.Rounds);                         // unchanged: CA drives the generic handler off its predicates
    }
}
