using System.Linq;
using System.Numerics;
using VortexArena.Formats.Sidecars;
using VortexArena.Common.Math;
using VortexArena.Engine.Simulation;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Tests the player-model skeletal-rendering support (REMAINING-WORK §6 follow-up): the model-info sidecar
/// parser + its resolution to a <see cref="PlayerSkeletonConfig"/>, and the locomotion blend that synthesizes
/// the upper/lower split <see cref="SkeletonAnim"/> from a single networked frame.
/// </summary>
public class ModelInfoAndBlendTests
{
    private const string Erebus =
        "name Erebus\n" +
        "species human\n" +
        "sex Male\n" +
        "description Heavyweight Xonotic Soldier\n" +
        "bone_upperbody spine2\n" +
        "bone_aim0 0.25 spine2\n" +
        "bone_aim1 0.4 spine4\n" +
        "bone_aim2 0.2 upperarm_L\n" +
        "bone_aim3 0.35 bip01 r hand\n" +
        "bone_weapon bip01 r hand\n" +
        "fixbone 1\n";

    [Fact]
    public void ModelInfo_Parses_Bones_Weights_And_SpacedNames()
    {
        ModelInfo info = ModelInfoParser.Parse(Erebus);
        Assert.Equal("Erebus", info.Name);
        Assert.Equal("spine2", info.BoneUpperBody);
        Assert.Equal("bip01 r hand", info.BoneWeapon);  // a bone name WITH spaces
        Assert.True(info.FixBone);

        Assert.Equal(4, info.AimBones.Count);
        Assert.Equal((0.25f, "spine2"), info.AimBones[0]);
        Assert.Equal((0.4f, "spine4"), info.AimBones[1]);
        Assert.Equal((0.35f, "bip01 r hand"), info.AimBones[3]); // weight token + spaced name
    }

    // A flat skeleton whose bone NAMES match the model-info so resolution can be checked. (1-based numbers:
    // root=1, spine2=2, spine4=3, upperarm_L=4, "bip01 r hand"=5.)
    private sealed class NamedModel : ISkeletalModel
    {
        private static readonly string[] Names = { "root", "spine2", "spine4", "upperarm_L", "bip01 r hand" };
        public int BoneCount => Names.Length;
        public string BoneName(int b) => Names[b];
        public int BoneParent(int b) => b == 0 ? -1 : 0;
        public BoneMatrix BindRelative(int b) => BoneMatrix.Identity;
        public int FrameCount => 0;
        public BoneMatrix FrameRelative(int frame, int b) => BoneMatrix.Identity;
    }

    [Fact]
    public void PlayerSkeletonConfig_FromModelInfo_ResolvesNamesToBoneIndices()
    {
        var mgr = new SkeletonManager();
        int s = mgr.Create(new NamedModel());
        PlayerSkeletonConfig cfg = PlayerSkeletonConfig.FromModelInfo(ModelInfoParser.Parse(Erebus), mgr, s);

        Assert.Equal(2, cfg.BoneUpperBody);             // spine2
        Assert.Equal(5, cfg.BoneWeapon);                // "bip01 r hand"
        Assert.True(cfg.FixBone);
        Assert.Equal(4, cfg.AimBones.Count);
        Assert.Equal((2, 0.25f), cfg.AimBones[0]);      // spine2 weight 0.25
        Assert.Equal((5, 0.35f), cfg.AimBones[3]);      // "bip01 r hand" weight 0.35
    }

    [Fact]
    public void PlayerSkeletonConfig_DropsUnresolvableBones()
    {
        string info = "bone_upperbody nope\nbone_aim0 0.5 spine2\nbone_aim1 0.5 ghost\nfixbone 1\n";
        var mgr = new SkeletonManager();
        int s = mgr.Create(new NamedModel());
        PlayerSkeletonConfig cfg = PlayerSkeletonConfig.FromModelInfo(ModelInfoParser.Parse(info), mgr, s);

        Assert.Equal(0, cfg.BoneUpperBody);             // "nope" -> unresolved
        Assert.False(cfg.FixBone);                      // fixbone forced off when there's no split bone
        Assert.Single(cfg.AimBones);                    // only spine2 resolved; "ghost" dropped
        Assert.Equal((2, 0.5f), cfg.AimBones[0]);
    }

    [Fact]
    public void SampleClip_Brackets_Frames_And_Lerps()
    {
        // a 4-frame looping clip at 10 fps starting at frame 30.
        var clip = new FrameGroup(firstFrame: 30, frameCount: 4, fps: 10f, loop: true);

        // phase 0 -> frame 30, lerp 0 (frameB advances to 31 but lerp 0 resolves to frame 30).
        Assert.Equal((30, 31, 0f), LocomotionBlend.SampleClip(clip, 0f));
        // t=0.15s -> phase 1.5 -> frames 31..32 at lerp 0.5
        (int a, int b, float l) = LocomotionBlend.SampleClip(clip, 0.15f);
        Assert.Equal(31, a); Assert.Equal(32, b); Assert.True(System.MathF.Abs(l - 0.5f) < 1e-4f);
        // looping wraps: t=0.35s -> phase 3.5 -> frame 33 .. wrap to 30
        (a, b, l) = LocomotionBlend.SampleClip(clip, 0.35f);
        Assert.Equal(33, a); Assert.Equal(30, b);

        // a single-frame clip never moves; a non-looping clip clamps at the last frame.
        Assert.Equal((5, 5, 0f), LocomotionBlend.SampleClip(new FrameGroup(5, 1, 10f, true), 99f));
        (a, b, _) = LocomotionBlend.SampleClip(new FrameGroup(0, 3, 10f, loop: false), 99f);
        Assert.Equal(2, a); Assert.Equal(2, b);
    }

    [Fact]
    public void Split_RoutesLegsAndTorso_ThroughTheUpperLowerSplit()
    {
        // legs clip frames 0..3, torso clip frames 10..11; sample both.
        var legs = new FrameGroup(0, 4, 10f, true);
        var torso = new FrameGroup(10, 2, 10f, true);
        SkeletonAnim anim = LocomotionBlend.Split(legs, 0.15f, torso, 0f);

        Assert.Equal(1, anim.Frame);    // legs current (phase 1.5 -> floor 1)
        Assert.Equal(2, anim.Frame2);   // legs next
        Assert.Equal(10, anim.Frame3);  // torso current (the upper body)
        Assert.True(System.MathF.Abs(anim.Lerp - 0.25f) < 1e-4f);  // 0.5 phase * 0.5 (split doubles it)
        Assert.Equal(0.5f, anim.Lerp3); // upper fully = frame3 after the split doubles it
        Assert.Equal(0f, anim.Lerp4);

        // Drive a real skeleton: the lower body takes the legs frame, the upper body the torso frame.
        var model = new SplitModel();
        var mgr = new SkeletonManager();
        var cfg = new PlayerSkeletonConfig { BoneUpperBody = 3 }; // head = upper; root+spine = lower
        var ps = new PlayerSkeleton(mgr, model, cfg);
        ps.FromFrames(LocomotionBlend.Split(model.Legs, 0f, model.Torso, 0f), viewPitch: 0f, isDead: true);

        // spine (lower, bone 2) shows the legs frame's spine pose; head (upper, bone 3) shows the torso frame.
        AssertVec(new Vector3(7, 0, 0), ps.GetBoneRel(2).Origin, 1e-3f);   // legs frame moved spine +x by 7
        AssertVec(new Vector3(0, 9, 0), ps.GetBoneRel(3).Origin, 1e-3f);   // torso frame moved head +y by 9
    }

    [Theory]
    [InlineData(0f, true, false, false, LocomotionBlend.Locomotion.Idle)]
    [InlineData(100f, true, false, false, LocomotionBlend.Locomotion.Walk)]
    [InlineData(360f, true, false, false, LocomotionBlend.Locomotion.Run)]
    [InlineData(360f, false, false, false, LocomotionBlend.Locomotion.Jump)]
    [InlineData(0f, true, true, false, LocomotionBlend.Locomotion.Crouch)]
    [InlineData(360f, true, false, true, LocomotionBlend.Locomotion.Dead)]
    public void SelectLegs_FromMovement(float speed, bool onGround, bool ducked, bool dead, LocomotionBlend.Locomotion expected)
        => Assert.Equal(expected, LocomotionBlend.SelectLegs(speed, onGround, ducked, dead));

    // Faithful animdecide 8-direction locomotion (animdecide.qc setimplicitstate + getloweranim). Angles are
    // yaw-only (pitch/roll 0) so makevectors gives forward=+x, right=-y in Quake convention (right.Y = -1 at
    // yaw 0). Velocity directions below are chosen so the dot-products land cleanly in each octant.
    [Theory]
    // dead / in-air win regardless of velocity.
    [InlineData(300f, 0f, false, false, true, LocomotionBlend.DirLocomotion.Dead)]
    [InlineData(300f, 0f, false, false, false, LocomotionBlend.DirLocomotion.Jump)]
    [InlineData(0f, 0f, false, true, false, LocomotionBlend.DirLocomotion.DuckJump)]
    // standing 8-direction (yaw 0: +x = forward, -x = back; +y = LEFT-key (right vector is -y so vy<0), ...).
    [InlineData(300f, 0f, true, false, false, LocomotionBlend.DirLocomotion.Run)]            // forward
    [InlineData(-300f, 0f, true, false, false, LocomotionBlend.DirLocomotion.RunBackwards)]  // backwards
    // slow (<=10 u/s 2D) => no direction bits => idle.
    [InlineData(5f, 0f, true, false, false, LocomotionBlend.DirLocomotion.Idle)]
    // ducked variants.
    [InlineData(300f, 0f, true, true, false, LocomotionBlend.DirLocomotion.DuckWalk)]            // ducked forward
    [InlineData(-300f, 0f, true, true, false, LocomotionBlend.DirLocomotion.DuckWalkBackwards)]  // ducked back
    [InlineData(5f, 0f, true, true, false, LocomotionBlend.DirLocomotion.DuckIdle)]              // ducked still
    public void SelectLegsDirectional_Forward_Back_Idle(float vx, float vy, bool onGround, bool ducked, bool dead,
        LocomotionBlend.DirLocomotion expected)
    {
        var vel = new Vector3(vx, vy, 0f);
        Assert.Equal(expected, LocomotionBlend.SelectLegsDirectional(vel, Vector3.Zero, onGround, ducked, dead));
    }

    [Fact]
    public void SelectLegsDirectional_Strafe_And_Diagonals()
    {
        // yaw 0: makevectors right = (0,-1,0). A +y velocity dots NEGATIVE onto right => LEFT key; -y => RIGHT.
        var left = new Vector3(0f, 300f, 0f);
        var right = new Vector3(0f, -300f, 0f);
        Assert.Equal(LocomotionBlend.DirLocomotion.StrafeLeft,
            LocomotionBlend.SelectLegsDirectional(left, Vector3.Zero, true, false, false));
        Assert.Equal(LocomotionBlend.DirLocomotion.StrafeRight,
            LocomotionBlend.SelectLegsDirectional(right, Vector3.Zero, true, false, false));

        // forward+left diagonal (equal magnitudes => both bits set via the 0.5 cot threshold).
        var fwdLeft = new Vector3(300f, 300f, 0f);
        Assert.Equal(LocomotionBlend.DirLocomotion.ForwardLeft,
            LocomotionBlend.SelectLegsDirectional(fwdLeft, Vector3.Zero, true, false, false));
        // back+right diagonal.
        var backRight = new Vector3(-300f, -300f, 0f);
        Assert.Equal(LocomotionBlend.DirLocomotion.BackRight,
            LocomotionBlend.SelectLegsDirectional(backRight, Vector3.Zero, true, false, false));

        // ducked diagonal.
        Assert.Equal(LocomotionBlend.DirLocomotion.DuckWalkForwardLeft,
            LocomotionBlend.SelectLegsDirectional(fwdLeft, Vector3.Zero, true, true, false));
    }

    // ============================================================================================
    //  Animation crossfade (Base csqcmodel cl_lerpanim_maxdelta_framegroups)
    // ============================================================================================

    [Fact]
    public void Split_TorsoLerpsWithinItsClip()
    {
        // The torso used to hold its integer frame (Lerp4 pinned 0 → the upper idle sway was frozen);
        // now both channels get within-clip interpolation.
        var legs = new FrameGroup(0, 4, 10f, loop: true);
        var torso = new FrameGroup(10, 2, 10f, loop: true);
        SkeletonAnim anim = LocomotionBlend.Split(legs, 0f, torso, 0.05f); // torso phase 0.5
        Assert.Equal(10, anim.Frame3);
        Assert.Equal(11, anim.Frame4);
        Assert.True(System.MathF.Abs(anim.Lerp3 - 0.25f) < 1e-4f); // (1−0.5)·0.5
        Assert.True(System.MathF.Abs(anim.Lerp4 - 0.25f) < 1e-4f); // 0.5·0.5

        // The action overload is the same math now (kept for its call-site tag).
        SkeletonAnim viaAction = LocomotionBlend.Split(legs, 0f, torso, 0.05f, _actionTag: true);
        Assert.Equal(anim.Frame3, viaAction.Frame3);
        Assert.Equal(anim.Lerp3, viaAction.Lerp3);
        Assert.Equal(anim.Lerp4, viaAction.Lerp4);
    }

    [Fact]
    public void ChannelFade_RampsOldWeightDown_AndAdvancesThePrevPlayhead()
    {
        var fade = new AnimChannelFade();
        Assert.Equal(0f, fade.RetainAndAdvance(0.1f, 0.016f)); // idle by default

        var clip = new FrameGroup(0, 4, 10f, loop: true);
        fade.Begin(clip, playhead: 0.35f, window: 0.1f, dt: 0.016f, maxStep: 0.25f);
        Assert.True(fade.Active(0.1f));

        // Switch frame renders the OLD pose at weight 1 (Base: l13 = 0 at the switch instant)…
        Assert.Equal(1f, fade.RetainAndAdvance(0.1f, 0.025f));
        Assert.True(System.MathF.Abs(fade.PrevTime - 0.375f) < 1e-5f); // the outgoing clip keeps playing
        // …then ramps linearly: age 0.025/0.05/0.075 → 0.75/0.5/0.25 — and completes exactly at the window.
        Assert.True(System.MathF.Abs(fade.RetainAndAdvance(0.1f, 0.025f) - 0.75f) < 1e-4f);
        Assert.True(System.MathF.Abs(fade.RetainAndAdvance(0.1f, 0.025f) - 0.50f) < 1e-4f);
        Assert.True(System.MathF.Abs(fade.RetainAndAdvance(0.1f, 0.025f) - 0.25f) < 1e-4f);
        Assert.Equal(0f, fade.RetainAndAdvance(0.1f, 0.025f));
        Assert.False(fade.Active(0.1f));
    }

    [Fact]
    public void ChannelFade_MidFadeSwitchReplacesHistory_SpikesAndZeroWindowSnap()
    {
        var fade = new AnimChannelFade();
        var clipA = new FrameGroup(0, 4, 10f, loop: true);
        var clipB = new FrameGroup(10, 4, 10f, loop: true);

        // A switch mid-fade drops the older history (Base keeps ONE previous framegroup per channel).
        fade.Begin(clipA, 1f, 0.1f, 0.016f, 0.25f);
        fade.RetainAndAdvance(0.1f, 0.05f);
        fade.Begin(clipB, 2f, 0.1f, 0.016f, 0.25f);
        Assert.Equal(10, fade.PrevClip.FirstFrame);
        Assert.Equal(1f, fade.RetainAndAdvance(0.1f, 0.016f)); // the new fade restarts at full old weight

        // A dt spike (hitch / long cull gap) snaps instead of fading from an ancient pose.
        fade.Begin(clipA, 0f, 0.1f, dt: 0.5f, maxStep: 0.25f);
        Assert.False(fade.Active(0.1f));
        // Window 0 = the Base-faithful hard-snap escape hatch.
        fade.Begin(clipA, 0f, window: 0f, dt: 0.016f, maxStep: 0.25f);
        Assert.False(fade.Active(0f));
        // An empty outgoing clip (nothing was showing yet) never fades.
        fade.Begin(default, 0f, 0.1f, 0.016f, 0.25f);
        Assert.False(fade.Active(0.1f));
        // Cancel kills an active fade (freeze snaps).
        fade.Begin(clipA, 0f, 0.1f, 0.016f, 0.25f);
        fade.Cancel();
        Assert.Equal(0f, fade.RetainAndAdvance(0.1f, 0.016f));
    }

    [Fact]
    public void FromFrames_Crossfade_BlendsOldIntoNew_PerChannelIndependently()
    {
        var model = new CrossfadeModel();
        var mgr = new SkeletonManager();
        var cfg = new PlayerSkeletonConfig { BoneUpperBody = 3 }; // head = upper; root+spine = lower
        var ps = new PlayerSkeleton(mgr, model, cfg);

        SkeletonAnim cur = LocomotionBlend.Split(model.LegsNew, 0f, model.TorsoNew, 0f);
        SkeletonAnim prev = LocomotionBlend.Split(model.LegsOld, 0f, model.TorsoOld, 0f);

        // legs half-faded, torso quarter-faded: spine = lerp(17, 7, 0.5) = 12; head = lerp(19, 9, 0.25) = 16.5.
        ps.FromFrames(cur, prev, legsRetain: 0.5f, torsoRetain: 0.25f, viewPitch: 0f, isDead: true);
        AssertVec(new Vector3(12f, 0, 0), ps.GetBoneRel(2).Origin, 1e-3f);
        AssertVec(new Vector3(0, 16.5f, 0), ps.GetBoneRel(3).Origin, 1e-3f);

        // Only the legs fading leaves the torso channel bit-identical to the unfaded pose.
        ps.FromFrames(cur, prev, legsRetain: 0.5f, torsoRetain: 0f, viewPitch: 0f, isDead: true);
        AssertVec(new Vector3(12f, 0, 0), ps.GetBoneRel(2).Origin, 1e-3f);
        AssertVec(new Vector3(0, 19f, 0), ps.GetBoneRel(3).Origin, 1e-3f);
    }

    [Fact]
    public void Build_RetainBlend_RescuesAntiparallelCollapse()
    {
        // Frame 0 = identity basis; frame 1 = 180° about Z (Fwd/Left both negated). Lerping them 50/50
        // nulls two basis columns — the exact degeneracy that made PushBones' AffineInverse throw
        // "determinant is zero" mid-crossfade. The retain path must fall back to the fresh pose instead.
        var model = new FlipModel();
        var mgr = new SkeletonManager();
        int skel = mgr.Create(model);

        mgr.Build(skel, new SkeletonAnim(frame: 1), model, retainfrac: 0f, 1, 1);   // seed the OLD pose (flipped)
        mgr.Build(skel, new SkeletonAnim(frame: 0), model, retainfrac: 0.5f, 1, 1); // blend in the NEW (identity)

        BoneMatrix m = mgr.GetBoneRel(skel, 1);
        AssertVec(new Vector3(1, 0, 0), m.Fwd, 1e-4f);  // rescued: the fresh pose, not a zero column
        AssertVec(new Vector3(0, 1, 0), m.Left, 1e-4f);

        // A healthy retain blend still lerps (identity vs identity-translated: origin midpoint).
        mgr.Build(skel, new SkeletonAnim(frame: 2), model, retainfrac: 0f, 1, 1);
        mgr.Build(skel, new SkeletonAnim(frame: 0), model, retainfrac: 0.5f, 1, 1);
        AssertVec(new Vector3(4, 0, 0), mgr.GetBoneRel(skel, 1).Origin, 1e-4f);
    }

    private sealed class FlipModel : ISkeletalModel
    {
        public int BoneCount => 1;
        public string BoneName(int b) => "root";
        public int BoneParent(int b) => -1;
        public BoneMatrix BindRelative(int b) => BoneMatrix.Identity;
        public int FrameCount => 3;
        public BoneMatrix FrameRelative(int frame, int b) => frame switch
        {
            1 => new BoneMatrix(new Vector3(-1, 0, 0), new Vector3(0, -1, 0), new Vector3(0, 0, 1), Vector3.Zero),
            2 => BoneMatrix.FromTRS(new Vector3(8, 0, 0), Quaternion.Identity, Vector3.One),
            _ => BoneMatrix.Identity,
        };
    }

    [Fact]
    public void FromFrames_Crossfade_RetainZero_IsBitIdenticalToThePlainForm()
    {
        var mgr1 = new SkeletonManager();
        var mgr2 = new SkeletonManager();
        var model = new CrossfadeModel();
        var cfg = new PlayerSkeletonConfig { BoneUpperBody = 3 };
        var a = new PlayerSkeleton(mgr1, model, cfg);
        var b = new PlayerSkeleton(mgr2, model, cfg);

        SkeletonAnim cur = LocomotionBlend.Split(model.LegsNew, 0f, model.TorsoNew, 0f);
        SkeletonAnim prev = LocomotionBlend.Split(model.LegsOld, 0f, model.TorsoOld, 0f);
        a.FromFrames(cur, viewPitch: 15f, isDead: false);
        b.FromFrames(cur, prev, 0f, 0f, viewPitch: 15f, isDead: false);

        for (int bone = 1; bone <= 3; bone++)
        {
            BoneMatrix ma = a.GetBoneRel(bone), mb = b.GetBoneRel(bone);
            AssertVec(ma.Origin, mb.Origin, 1e-6f);
            AssertVec(ma.Fwd, mb.Fwd, 1e-6f);
            AssertVec(ma.Left, mb.Left, 1e-6f);
            AssertVec(ma.Up, mb.Up, 1e-6f);
        }
    }

    // root(0) -> spine(1) -> head(2); frames: 0 spine+x7 (legs OLD), 1 head+y9 (torso OLD),
    // 2 spine+x17 (legs NEW), 3 head+y19 (torso NEW). One-frame clips per pose for exact expectations.
    private sealed class CrossfadeModel : ISkeletalModel
    {
        public readonly FrameGroup LegsOld = new(0, 1, 10f, true);
        public readonly FrameGroup TorsoOld = new(1, 1, 10f, true);
        public readonly FrameGroup LegsNew = new(2, 1, 10f, true);
        public readonly FrameGroup TorsoNew = new(3, 1, 10f, true);
        public int BoneCount => 3;
        public string BoneName(int b) => b switch { 0 => "root", 1 => "spine", 2 => "head", _ => "" };
        public int BoneParent(int b) => b - 1;
        public BoneMatrix BindRelative(int b) => BoneMatrix.Identity;
        public int FrameCount => 4;
        public BoneMatrix FrameRelative(int frame, int b) => (frame, b) switch
        {
            (0, 1) => Trans(7, 0, 0),
            (1, 2) => Trans(0, 9, 0),
            (2, 1) => Trans(17, 0, 0),
            (3, 2) => Trans(0, 19, 0),
            _ => BoneMatrix.Identity,
        };
        private static BoneMatrix Trans(float x, float y, float z)
            => BoneMatrix.FromTRS(new Vector3(x, y, z), Quaternion.Identity, Vector3.One);
    }

    // root(0) -> spine(1) -> head(2); legs frame (index 0) shifts spine +x7, torso frame (index 1) shifts head +y9.
    private sealed class SplitModel : ISkeletalModel
    {
        public readonly FrameGroup Legs = new(0, 1, 10f, true);
        public readonly FrameGroup Torso = new(1, 1, 10f, true);
        public int BoneCount => 3;
        public string BoneName(int b) => b switch { 0 => "root", 1 => "spine", 2 => "head", _ => "" };
        public int BoneParent(int b) => b - 1;
        public BoneMatrix BindRelative(int b) => BoneMatrix.Identity;
        public int FrameCount => 2;
        public BoneMatrix FrameRelative(int frame, int b) => (frame, b) switch
        {
            (0, 1) => Trans(7, 0, 0),   // legs frame: spine +x
            (1, 2) => Trans(0, 9, 0),   // torso frame: head +y
            _ => BoneMatrix.Identity,
        };
        private static BoneMatrix Trans(float x, float y, float z)
            => BoneMatrix.FromTRS(new Vector3(x, y, z), Quaternion.Identity, Vector3.One);
    }

    private static void AssertVec(Vector3 expected, Vector3 actual, float tol = 1e-4f)
        => Assert.True((expected - actual).Length() <= tol, $"expected {expected}, got {actual} (tol {tol})");
}
