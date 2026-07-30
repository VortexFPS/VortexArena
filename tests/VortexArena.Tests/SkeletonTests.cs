using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using XonoticGodot.Formats.Iqm;
using XonoticGodot.Formats.Vfs;
using XonoticGodot.Common.Math;
using XonoticGodot.Engine.Simulation;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests the skeletal CPU-manipulation builtins (<see cref="SkeletonManager"/> = the QC <c>skel_*</c>) and the
/// player upper/lower-body split + view-pitch aim port (<see cref="PlayerSkeleton"/>, from
/// <c>player_skeleton.qc</c>) — REMAINING-WORK §6 "Skeletal skel_* CPU manipulation". A synthetic 3-bone chain
/// exercises every primitive deterministically; a shipped IQM is a smoke test of the <see cref="IqmSkeletalModel"/>
/// adapter.
/// </summary>
public class SkeletonTests
{
    private const string DataDir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";

    /// <summary>A 3-bone chain root(0) → spine(1) → head(2), each 16u up its parent, plus two frames that
    /// shift the spine (+x) / head (+y) so the upper/lower split is observable.</summary>
    private sealed class ChainModel : ISkeletalModel
    {
        public int BoneCount => 3;
        public string BoneName(int b) => b switch { 0 => "root", 1 => "spine", 2 => "head", _ => "" };
        public int BoneParent(int b) => b - 1;          // root -1, spine 0, head 1
        public BoneMatrix BindRelative(int b) => Trans(0, 0, b == 0 ? 0 : 16);
        public int FrameCount => 3;
        public BoneMatrix FrameRelative(int frame, int b) => (frame, b) switch
        {
            (1, 1) => Trans(10, 0, 16),  // frame 1: spine shifted +x
            (2, 2) => Trans(0, 5, 16),   // frame 2: head shifted +y
            _ => BindRelative(b),
        };
        private static BoneMatrix Trans(float x, float y, float z)
            => BoneMatrix.FromTRS(new Vector3(x, y, z), Quaternion.Identity, Vector3.One);
    }

    [Fact]
    public void Skel_Create_Query_Hierarchy()
    {
        var mgr = new SkeletonManager();
        int s = mgr.Create(new ChainModel());
        Assert.True(s >= 1);
        Assert.Equal(3, mgr.GetNumBones(s));
        Assert.Equal("spine", mgr.GetBoneName(s, 2));
        Assert.Equal(2, mgr.GetBoneParent(s, 3));     // head's parent is spine (1-based 2)
        Assert.Equal(0, mgr.GetBoneParent(s, 1));     // root has no parent (0)
        Assert.Equal(3, mgr.FindBone(s, "head"));
        Assert.Equal(0, mgr.FindBone(s, "nope"));

        Assert.Equal(0, mgr.Create(null));            // non-skeletal model -> 0
        mgr.Delete(s);
        Assert.Equal(0, mgr.GetNumBones(s));          // deleted
    }

    [Fact]
    public void Skel_Build_PosesBonesAndComposesAbs()
    {
        var mgr = new SkeletonManager();
        var model = new ChainModel();
        int s = mgr.Create(model);

        // build frame 0 (== bind) fully (lerp 0 -> weight on frame1 only)
        mgr.Build(s, new SkeletonAnim(frame: 0), model, retainfrac: 0f, firstbone: 1, lastbone: 3);

        // relative transforms == bind translations
        AssertVec(new Vector3(0, 0, 16), mgr.GetBoneRel(s, 2).Origin);
        // head absolute = root∘spine∘head origins stacked: (0,0,0)+(0,0,16)+(0,0,16) = (0,0,32)
        AssertVec(new Vector3(0, 0, 32), mgr.GetBoneAbs(s, 3).Origin, 1e-3f);

        // retainfrac 1.0 keeps the old pose (no change); 0.5 blends halfway
        BoneMatrix before = mgr.GetBoneRel(s, 2);
        mgr.Build(s, new SkeletonAnim(frame: 1), model, retainfrac: 1f, firstbone: 1, lastbone: 3);
        AssertVec(before.Origin, mgr.GetBoneRel(s, 2).Origin, 1e-4f);          // retain=1 -> unchanged

        mgr.Build(s, new SkeletonAnim(frame: 1), model, retainfrac: 0.5f, firstbone: 1, lastbone: 3);
        AssertVec(new Vector3(5, 0, 16), mgr.GetBoneRel(s, 2).Origin, 1e-3f);  // halfway to (10,0,16)
    }

    [Fact]
    public void Skel_Set_Mul_Copy_Bones()
    {
        var mgr = new SkeletonManager();
        var model = new ChainModel();
        int s = mgr.Create(model);

        // set bone 2 to a known matrix
        mgr.SetBone(s, 2, BoneMatrix.FromTRS(new Vector3(1, 2, 3), Quaternion.Identity, Vector3.One));
        AssertVec(new Vector3(1, 2, 3), mgr.GetBoneRel(s, 2).Origin);

        // mul by a translation-only matrix: relative = mulMatrix ∘ relative -> origin = mul.Transform(rel.origin)
        mgr.MulBone(s, 2, BoneMatrix.FromTRS(new Vector3(0, 0, 10), Quaternion.Identity, Vector3.One));
        AssertVec(new Vector3(1, 2, 13), mgr.GetBoneRel(s, 2).Origin, 1e-4f);

        // copy bone 2 into a second skeleton
        int s2 = mgr.Create(model);
        mgr.CopyBones(s2, s, 1, 3);
        AssertVec(new Vector3(1, 2, 13), mgr.GetBoneRel(s2, 2).Origin, 1e-4f);
    }

    [Fact]
    public void PlayerSkeleton_UpperLowerSplit_RoutesFramesByBoneType()
    {
        var mgr = new SkeletonManager();
        var model = new ChainModel();
        var cfg = new PlayerSkeletonConfig { BoneUpperBody = 3 }; // head is the split bone -> head=UPPER, root+spine=LOWER
        var ps = new PlayerSkeleton(mgr, model, cfg);

        // upper body shows frames 1+3, lower shows frames 2+4. Choose lerps so upper fully uses frame3 (index 2)
        // and lower fully uses frame2 (index 1):  saveLerp=0.5, saveLerp3=0.5, saveLerp4=0.
        var anim = new SkeletonAnim(frame: 0, frame2: 1, frame3: 2, frame4: 0, lerp: 0.5f, lerp3: 0.5f, lerp4: 0f);
        ps.FromFrames(anim, viewPitch: 0f, isDead: true); // isDead skips the aim bones

        // spine (LOWER, bone 2) took frame 2 == model frame index 1 (spine shifted +x)
        AssertVec(new Vector3(10, 0, 16), ps.GetBoneRel(2).Origin, 1e-3f);
        // head (UPPER, bone 3) took frame 3 == model frame index 2 (head shifted +y)
        AssertVec(new Vector3(0, 5, 16), ps.GetBoneRel(3).Origin, 1e-3f);
    }

    [Fact]
    public void PlayerSkeleton_AimBone_BendsByViewPitch()
    {
        var mgr = new SkeletonManager();
        var model = new ChainModel();
        var cfg = new PlayerSkeletonConfig { BoneUpperBody = 3 };
        cfg.AimBones.Add((bone: 3, weight: 1.0f)); // the head aims fully by the view pitch
        var ps = new PlayerSkeleton(mgr, model, cfg);

        // identity-rotation frames -> head's absolute forward is +X before aiming.
        var anim = new SkeletonAnim(frame: 0, frame2: 0, frame3: 0, frame4: 0, lerp: 0.5f, lerp3: 0.5f, lerp4: 0f);

        ps.FromFrames(anim, viewPitch: 0f, isDead: false);
        AssertVec(new Vector3(1, 0, 0), Vector3.Normalize(ps.GetBoneAbs(3).Fwd), 1e-3f); // pitch 0 -> still +X

        ps.FromFrames(anim, viewPitch: 30f, isDead: false);
        Vector3 fwd = Vector3.Normalize(ps.GetBoneAbs(3).Fwd);
        // looking down 30°: the head forward tilts down by 30° (forward.z = -sin30 = -0.5)
        Assert.True(fwd.Z < -0.4f && fwd.Z > -0.6f, $"expected forward.z ≈ -0.5 (pitch 30 down), got {fwd}");
        Assert.True(fwd.X > 0.8f, $"expected forward still mostly +X, got {fwd}");
    }

    [Fact]
    public void Iqm_RealModel_Builds_A_Skeleton()
    {
        if (!Directory.Exists(DataDir)) return;
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountGameDir(DataDir)) return;
        // find an IQM that actually has a skeleton with several bones (player/weapon models)
        IqmData? skeletal = null;
        foreach (string path in vfs.Find("models/", "iqm").Take(80))
        {
            var iqm = IqmReader.Read(vfs.ReadBytes(path));
            if (iqm.Joints.Length >= 2) { skeletal = iqm; break; }
        }
        if (skeletal is null) return;

        var model = new IqmSkeletalModel(skeletal);
        var mgr = new SkeletonManager();
        int s = mgr.Create(model);
        Assert.True(s >= 1);
        Assert.Equal(skeletal.Joints.Length, mgr.GetNumBones(s));

        mgr.Build(s, new SkeletonAnim(frame: 0), model, retainfrac: 0f, firstbone: 1, lastbone: 100000);

        // every bone's absolute transform is finite and its parent index is < its own (IQM ordering).
        for (int b = 1; b <= mgr.GetNumBones(s); b++)
        {
            Vector3 o = mgr.GetBoneAbs(s, b).Origin;
            Assert.False(float.IsNaN(o.X) || float.IsNaN(o.Y) || float.IsNaN(o.Z), $"bone {b} abs is NaN");
            Assert.True(mgr.GetBoneParent(s, b) < b, $"bone {b} parent must precede it");
        }
    }

    private static void AssertVec(Vector3 expected, Vector3 actual, float tol = 1e-4f)
        => Assert.True((expected - actual).Length() <= tol, $"expected {expected}, got {actual} (tol {tol})");
}
