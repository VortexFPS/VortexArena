using XonoticGodot.Common.Framework;
using XonoticGodot.Common.Gameplay;
using XonoticGodot.Common.Services;
using XonoticGodot.Engine.Collision;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests for the Crylink "converge on release" link-join gate (<see cref="Crylink.WrThink"/> — port of
/// crylink.qc <c>wr_think</c>, lines ~518-545). The defining mechanic: after firing a join-enabled group the
/// spikes stay spread while the fire button is HELD and converge only once it's RELEASED, so the player can
/// time the convergence onto a target (crylink.qc <c>describe()</c>).
///
/// QC gates the join on BOTH (a) the button-release level check (<c>!(fire &amp; 1)</c> / <c>!(fire &amp; 2)</c>)
/// AND (b) a joindelay floor (<c>time &gt; teleport_time == firetime + joindelay</c>) — the joindelay is only a
/// minimum, the RELEASE is the trigger. These exercise that gate directly at the <c>WrThink</c> level: the
/// driver (<see cref="WeaponFireDriver"/>) records this tick's held buttons on the slot via
/// <c>SetButtons</c> and then calls <c>WrThink(Primary)</c> every tick, so setting
/// <see cref="WeaponSlotState.ButtonAttack"/>/<see cref="WeaponSlotState.ButtonAttack2"/> and calling
/// <c>WrThink(Primary)</c> reproduces exactly what the live driver does.
/// </summary>
[Collection("GlobalState")]
public class CrylinkLinkJoinTests
{
    // The slot's clock sits at t=0 (a fresh EngineServices GameClock), so a head spike's LTime (its fire
    // time, set when the group spawned) is chosen relative to 0: LTime well in the past => the joindelay floor
    // has elapsed; LTime at/after now => it has not.
    private const float JoinDelayElapsed = -1f;   // 0 > -1 + 0.1  -> floor passed
    private const float JoinDelayPending = 0f;     // 0 > 0 + 0.1   -> floor NOT passed

    private static (Crylink crylink, Entity actor, WeaponSlot slot, WeaponSlotState st) Setup()
    {
        // Empty cvar store -> Configure() takes the stock joindelay (primary 0.1s) fallbacks, same as a bare
        // server (matches WeaponBalanceTests). Api.Services is process-global, hence [Collection("GlobalState")].
        Api.Services = new EngineServices(new CollisionWorld());
        var crylink = new Crylink();
        crylink.Configure();

        var actor = new Entity();
        var slot = new WeaponSlot(0);
        return (crylink, actor, slot, actor.WeaponState(slot));
    }

    /// <summary>Stand-in for the last-fired group head: a live spike whose LTime puts the joindelay floor where asked.</summary>
    private static Entity GroupHead(float lifeStart) => new() { ClassName = "spike", LTime = lifeStart };

    // ---- primary group ----------------------------------------------------------------------------------

    [Fact]
    public void PrimaryGroup_ButtonHeld_DoesNotConverge_EvenAfterJoinDelay()
    {
        // THE BUG this fix targets: with the button still held, the spikes must stay spread indefinitely — even
        // though the joindelay floor has long since passed. (The old port converged here, 0.1s after firing.)
        var (crylink, actor, slot, st) = Setup();
        Entity head = GroupHead(JoinDelayElapsed);
        st.CrylinkWaitRelease = 1;
        st.CrylinkLastGroup = head;
        st.ButtonAttack = true;   // primary held this tick

        crylink.WrThink(actor, slot, FireMode.Primary);

        Assert.Equal(1, st.CrylinkWaitRelease);          // still waiting for release
        Assert.Same(head, st.CrylinkLastGroup);          // group untouched (not converged)
    }

    [Fact]
    public void PrimaryGroup_Released_AfterJoinDelay_Converges()
    {
        var (crylink, actor, slot, st) = Setup();
        st.CrylinkWaitRelease = 1;
        st.CrylinkLastGroup = GroupHead(JoinDelayElapsed);
        st.ButtonAttack = false;  // released

        crylink.WrThink(actor, slot, FireMode.Primary);

        Assert.Equal(0, st.CrylinkWaitRelease);          // converged -> wait-release cleared
        Assert.Null(st.CrylinkLastGroup);
    }

    [Fact]
    public void PrimaryGroup_Released_BeforeJoinDelay_StillWaits()
    {
        // Release alone isn't enough: the joindelay floor is the additional minimum (QC requires BOTH). The
        // group converges on the first later tick where the floor has also passed.
        var (crylink, actor, slot, st) = Setup();
        Entity head = GroupHead(JoinDelayPending);
        st.CrylinkWaitRelease = 1;
        st.CrylinkLastGroup = head;
        st.ButtonAttack = false;  // released, but floor not yet met

        crylink.WrThink(actor, slot, FireMode.Primary);

        Assert.Equal(1, st.CrylinkWaitRelease);          // floor not met -> keep waiting
        Assert.Same(head, st.CrylinkLastGroup);
    }

    [Fact]
    public void PrimaryGroup_Released_WithDeadHeadSpike_ClearsWaitRelease_NoStrand()
    {
        // Regression guard: the port's CrylinkLastGroup can dangle (a head spike removed on touch/fade gets
        // IsFreed = true, unlike QC which re-heads the queue). Releasing must still drop out of wait-release —
        // otherwise a dead group would leave CrylinkWaitRelease == 1 forever and block all further primary fire.
        var (crylink, actor, slot, st) = Setup();
        st.CrylinkWaitRelease = 1;
        st.CrylinkLastGroup = new Entity { ClassName = "spike", LTime = JoinDelayElapsed, IsFreed = true };
        st.ButtonAttack = false;  // released

        crylink.WrThink(actor, slot, FireMode.Primary);

        Assert.Equal(0, st.CrylinkWaitRelease);          // not stranded
        Assert.Null(st.CrylinkLastGroup);
    }

    // ---- secondary group --------------------------------------------------------------------------------
    // The link-join block lives in the every-tick WrThink(Primary) call, but reads st.ButtonAttack2 for a
    // secondary group (CrylinkWaitRelease == 2). The driver's SetButtons records this tick's ATK2 before the
    // Primary call even on ticks where WrThink(Secondary) isn't invoked, so ButtonAttack2 is the live release
    // authority for the secondary group too. These drive WrThink(Primary) to prove exactly that.

    [Fact]
    public void SecondaryGroup_Atck2Held_DoesNotConverge_InPrimaryThink()
    {
        var (crylink, actor, slot, st) = Setup();
        Entity head = GroupHead(JoinDelayElapsed);
        st.CrylinkWaitRelease = 2;
        st.CrylinkLastGroup = head;
        st.ButtonAttack = false;  // primary not held
        st.ButtonAttack2 = true;  // secondary STILL held -> stay spread

        crylink.WrThink(actor, slot, FireMode.Primary);

        Assert.Equal(2, st.CrylinkWaitRelease);
        Assert.Same(head, st.CrylinkLastGroup);
    }

    [Fact]
    public void SecondaryGroup_Atck2Released_Converges_InPrimaryThink()
    {
        var (crylink, actor, slot, st) = Setup();
        st.CrylinkWaitRelease = 2;
        st.CrylinkLastGroup = GroupHead(JoinDelayElapsed);
        st.ButtonAttack = false;
        st.ButtonAttack2 = false; // secondary released -> converge

        crylink.WrThink(actor, slot, FireMode.Primary);

        Assert.Equal(0, st.CrylinkWaitRelease);
        Assert.Null(st.CrylinkLastGroup);
    }
}
