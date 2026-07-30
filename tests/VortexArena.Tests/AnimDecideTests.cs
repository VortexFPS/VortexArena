using XonoticGodot.Common.Gameplay;
using XonoticGodot.Engine.Simulation;
using Xunit;
using A = XonoticGodot.Common.Gameplay.AnimDecide.AnimUpperAction;
using P = XonoticGodot.Common.Gameplay.AnimDecide.AnimPriority;

namespace XonoticGodot.Tests;

/// <summary>
/// [W14b Stage 3] Tests the Godot-free upper-body animdecide DECISION port (<see cref="AnimDecide"/>) and the
/// client torso-action selector (<see cref="LocomotionBlend.SelectTorsoAction"/>) — the SHARED CONTRACT the
/// server producer (the weapon-fire set-site) and the client consumer (the torso overlay) both reference.
/// Mirrors the existing LocomotionBlend test pattern in ModelInfoAndBlendTests.
/// </summary>
public class AnimDecideTests
{
    // Base animdecide.qc:78 — anim_shoot = animfixfps(e, ANIM_VEC(shoot, 1, 5)): numframes 1 @ 5 fps → 0.2s.
    private const float ShootDur = 1f / 5f;

    [Fact]
    public void SpecFor_Shoot_MatchesBaseShootWindow()
    {
        AnimDecide.AnimSpec spec = AnimDecide.SpecFor(A.Shoot);
        Assert.Equal(1, spec.NumFrames);
        Assert.Equal(5f, spec.FrameRate);
        Assert.Equal(ShootDur, spec.DurationSeconds, 5);
    }

    // [W14b Stage 4] each remaining action's running window == Base animdecide.qc ANIM_VEC(name, 1, rate) -> 1/rate s.
    [Theory]
    [InlineData(A.Draw, 1, 3f, 1f / 3f)]      // animdecide.qc:70 ANIM_VEC(draw, 1, 3)     -> 0.333s
    [InlineData(A.Pain1, 1, 2f, 0.5f)]        // animdecide.qc:76 ANIM_VEC(pain1, 1, 2)    -> 0.5s
    [InlineData(A.Pain2, 1, 2f, 0.5f)]        // animdecide.qc:77 ANIM_VEC(pain2, 1, 2)    -> 0.5s
    [InlineData(A.Melee, 1, 1f, 1f)]          // animdecide.qc:88 ANIM_VEC(melee, 1, 1)    -> 1.0s
    [InlineData(A.Taunt, 1, 0.33f, 1f / 0.33f)] // animdecide.qc:79 ANIM_VEC(taunt, 1, 0.33) -> ~3.03s
    [InlineData(A.Die1, 1, 0.5f, 2f)]         // animdecide.qc:68 ANIM_VEC(die1, 1, 0.5)   -> 2.0s
    [InlineData(A.Die2, 1, 0.5f, 2f)]         // animdecide.qc:69 ANIM_VEC(die2, 1, 0.5)   -> 2.0s
    public void SpecFor_Stage4Actions_MatchBaseWindows(A action, int numFrames, float rate, float dur)
    {
        AnimDecide.AnimSpec spec = AnimDecide.SpecFor(action);
        Assert.Equal(numFrames, spec.NumFrames);
        Assert.Equal(rate, spec.FrameRate);
        Assert.Equal(dur, spec.DurationSeconds, 4);
    }

    [Fact]
    public void GetUpperAnim_Idle_When_NoAction()
    {
        var (a, prio) = AnimDecide.GetUpperAnim(A.None, start: 0f, now: 10f);
        Assert.Equal(A.None, a);
        Assert.Equal(P.Idle, prio);
    }

    [Fact]
    public void GetUpperAnim_Shoot_Active_WithinWindow()
    {
        // started at t=10; at t=10.1 (within the 0.2s window) SHOOT is ACTIVE.
        var (a, prio) = AnimDecide.GetUpperAnim(A.Shoot, start: 10f, now: 10.1f);
        Assert.Equal(A.Shoot, a);
        Assert.Equal(P.Active, prio);
    }

    [Fact]
    public void GetUpperAnim_Shoot_Expires_To_Idle_After_Window()
    {
        // exactly at the boundary (start + dur) it is STILL active (QC uses <=); just past it expires to idle.
        Assert.Equal(P.Active, AnimDecide.GetUpperAnim(A.Shoot, 10f, 10f + ShootDur).priority);
        var (a, prio) = AnimDecide.GetUpperAnim(A.Shoot, 10f, 10f + ShootDur + 0.001f);
        Assert.Equal(A.None, a);
        Assert.Equal(P.Idle, prio);
    }

    [Fact]
    public void GetUpperAnim_Dead_Beats_Everything_And_Is_Not_Windowed()
    {
        // die1/die2 win regardless of velocity/time and are NEVER windowed (held until the state clears).
        Assert.Equal((A.Die1, P.Dead), AnimDecide.GetUpperAnim(A.Die1, start: 0f, now: 999f));
        Assert.Equal((A.Die2, P.Dead), AnimDecide.GetUpperAnim(A.Die2, start: 0f, now: 999f));
    }

    // [W14b Stage 4] each ACTIVE action is returned with ACTIVE priority within its window and expires to idle just
    // past it (QC `time <= start + numframes/framerate`), exactly like SHOOT.
    [Theory]
    [InlineData(A.Draw, 1f / 3f)]
    [InlineData(A.Pain1, 0.5f)]
    [InlineData(A.Pain2, 0.5f)]
    [InlineData(A.Melee, 1f)]
    [InlineData(A.Taunt, 1f / 0.33f)]
    public void GetUpperAnim_Stage4Action_Active_WithinWindow_Then_Expires(A action, float dur)
    {
        // strictly inside the window → ACTIVE; at the boundary still ACTIVE (QC <=); just past → idle/None.
        Assert.Equal((action, P.Active), AnimDecide.GetUpperAnim(action, 10f, 10f + dur * 0.5f));
        Assert.Equal(P.Active, AnimDecide.GetUpperAnim(action, 10f, 10f + dur).priority);
        var (a, prio) = AnimDecide.GetUpperAnim(action, 10f, 10f + dur + 0.01f);
        Assert.Equal(A.None, a);
        Assert.Equal(P.Idle, prio);
    }

    [Fact]
    public void GetUpperAnim_Death_Outranks_Pain_Cascade()
    {
        // The DEAD>ACTIVE>IDLE cascade: a DIE latch is held at DEAD priority forever, even at a `now` long past
        // when a PAIN window (0.5s) would have expired — so a dead player's death overlay never falls back to idle
        // and (since DIE is the only thing the producer sets on death) is never replaced by a late pain. Contrast a
        // PAIN latch at the same late time, which has expired to IDLE.
        Assert.Equal((A.Die1, P.Dead), AnimDecide.GetUpperAnim(A.Die1, start: 10f, now: 100f));
        Assert.Equal((A.Die2, P.Dead), AnimDecide.GetUpperAnim(A.Die2, start: 10f, now: 100f));
        // a PAIN at the same elapsed time is long gone (0.5s window) → IDLE, confirming PAIN can't outlive DEAD.
        Assert.Equal((A.None, P.Idle), AnimDecide.GetUpperAnim(A.Pain1, start: 10f, now: 100f));
    }

    [Fact]
    public void SetAction_Latches_Start_And_Does_Not_Restart_Same_Action()
    {
        // fresh latch stamps the start time.
        var (a1, s1) = AnimDecide.SetAction(A.None, 0f, A.Shoot, now: 10f);
        Assert.Equal(A.Shoot, a1);
        Assert.Equal(10f, s1);

        // re-latching the SAME action (no restart) keeps the original start (a held trigger doesn't re-window).
        var (a2, s2) = AnimDecide.SetAction(A.Shoot, 10f, A.Shoot, now: 10.05f);
        Assert.Equal(A.Shoot, a2);
        Assert.Equal(10f, s2);

        // restart = true forces a new start (the per-shot re-fire, QC restartanim).
        var (_, s3) = AnimDecide.SetAction(A.Shoot, 10f, A.Shoot, now: 10.05f, restart: true);
        Assert.Equal(10.05f, s3);
    }

    [Fact]
    public void SelectTorsoAction_None_Is_Inactive()
    {
        var (active, phase) = LocomotionBlend.SelectTorsoAction((byte)A.None, start: 0f, now: 5f);
        Assert.False(active);
        Assert.Equal(0f, phase);
    }

    [Fact]
    public void SelectTorsoAction_Shoot_Active_Returns_ElapsedPhase()
    {
        var (active, phase) = LocomotionBlend.SelectTorsoAction((byte)A.Shoot, start: 10f, now: 10.1f);
        Assert.True(active);
        Assert.Equal(0.1f, phase, 5);
    }

    [Fact]
    public void SelectTorsoAction_Shoot_Inactive_After_Window()
    {
        var (active, _) = LocomotionBlend.SelectTorsoAction((byte)A.Shoot, start: 10f, now: 10f + ShootDur + 0.01f);
        Assert.False(active);
    }

    [Fact]
    public void SelectTorsoAction_Die_Active_And_Never_Expires()
    {
        // [W14b Stage 4] the death torso overlay stays ACTIVE indefinitely (DIE is never windowed), so a dead player
        // keeps playing the death pose (its non-looping clip clamps at the last frame) until respawn clears the latch.
        var (active, phase) = LocomotionBlend.SelectTorsoAction((byte)A.Die1, start: 10f, now: 1000f);
        Assert.True(active);
        Assert.Equal(990f, phase, 3);
    }

    [Fact]
    public void Split_Action_Routes_ActionClip_Into_Frame3_4_And_Animates_Torso()
    {
        // legs clip frames 0..3, the SHOOT action clip frames 20..23 (non-looping), phase 0.05s @ 10 fps → 0.5 lerp.
        var legs = new XonoticGodot.Formats.Sidecars.FrameGroup(0, 4, 10f, true);
        var action = new XonoticGodot.Formats.Sidecars.FrameGroup(20, 4, 10f, loop: false);
        SkeletonAnim anim = LocomotionBlend.Split(legs, legsTime: 0.15f, action, actionPhase: 0.05f, _actionTag: true);

        // legs base unchanged (lower body): Frame/Frame2 from the legs clip.
        Assert.Equal(1, anim.Frame);   // legs current (phase 1.5 → floor 1)
        Assert.Equal(2, anim.Frame2);  // legs next
        // torso/action now WEIGHTED into the upper body via Frame3 (current) + Frame4 (next).
        Assert.Equal(20, anim.Frame3); // action current (phase 0.5 → floor 0 → frame 20)
        Assert.Equal(21, anim.Frame4); // action next
        // upper split: Lerp3 = (1−f)·0.5, Lerp4 = f·0.5, summing to 0.5 (doubled to 1.0 in FromFrames).
        Assert.Equal(0.25f, anim.Lerp3, 4); // (1−0.5)·0.5
        Assert.Equal(0.25f, anim.Lerp4, 4); // 0.5·0.5
    }

    [Fact]
    public void Split_Action_Clamps_NonLooping_Clip_At_LastFrame()
    {
        var legs = new XonoticGodot.Formats.Sidecars.FrameGroup(0, 4, 10f, true);
        var action = new XonoticGodot.Formats.Sidecars.FrameGroup(20, 4, 10f, loop: false);
        // a phase far past the clip's end clamps the non-looping action at its last frame (no wrap, holds the pose).
        SkeletonAnim anim = LocomotionBlend.Split(legs, 0f, action, actionPhase: 99f, _actionTag: true);
        Assert.Equal(23, anim.Frame3); // last action frame
        Assert.Equal(23, anim.Frame4); // clamped (no next)
        Assert.Equal(0f, anim.Lerp4, 4); // at the clamp the inter-frame fraction is 0
    }
}
