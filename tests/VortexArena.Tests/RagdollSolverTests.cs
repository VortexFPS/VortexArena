using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using VortexArena.Formats.Iqm;
using VortexArena.Formats.Vfs;
using VortexArena.Common.Math;
using VortexArena.Engine.Simulation;
using Xunit;

namespace VortexArena.Tests;

/// <summary>
/// Tests the corpse-ragdoll pieces: the pure Verlet <see cref="RagdollSolver"/> (sticks hold, floors stop,
/// it goes to sleep), the shortest-arc bone alignment, and <see cref="RagdollRig"/>'s name/topology
/// resolution against a synthetic stock-named skeleton plus a shipped player IQM (self-skips without assets).
/// </summary>
public class RagdollSolverTests
{
    // Was a hardcoded absolute path under Projects/Xonotic/.../assets/data. This test predates the shared
    // TestPaths helper (restructure G13, stage -1), and that literal pointed at a path that no longer
    // exists on ANY machine: the repo moved to Projects/Vortex/VortexArena and the content tree to data/.
    // TestPathsTests.No_Test_Hardcodes_A_Workstation_Path is what caught it during this merge.
    private static string DataDir => TestPaths.Data;

    private static RagdollTraceHit NoHit(Vector3 s, Vector3 e, float r) => new(e, Vector3.Zero, false);

    /// <summary>A flat floor at z = 0 (surface contact at the particle's half-extent).</summary>
    private static RagdollTraceHit FloorTrace(Vector3 s, Vector3 e, float r)
        => e.Z < r ? new(new Vector3(e.X, e.Y, r), new Vector3(0, 0, 1), true) : new(e, Vector3.Zero, false);

    [Fact]
    public void Sticks_HoldTheirRestLength_ThroughFreeFall()
    {
        var seeds = new[]
        {
            new Vector3(0, 0, 500), new Vector3(10, 0, 500), new Vector3(20, 0, 500), new Vector3(30, 0, 500),
        };
        var s = new RagdollSolver(seeds, NoHit);
        s.AddStick(0, 1); s.AddStick(1, 2); s.AddStick(2, 3);
        s.SetVelocity(new Vector3(120f, 40f, 90f));
        s.AddParticleVelocity(3, new Vector3(0, 0, 160f)); // asymmetric tumble stresses the constraints

        for (int i = 0; i < 60; i++)
            s.Step(1f / 60f);

        for (int i = 0; i < 3; i++)
        {
            float len = Vector3.Distance(s.Position(i), s.Position(i + 1));
            Assert.True(MathF.Abs(len - 10f) < 0.2f, $"stick {i} length {len}, expected ~10");
        }
    }

    [Fact]
    public void DroppedBody_RestsOnTheFloor_AndSleeps()
    {
        var seeds = new[] { new Vector3(0, 0, 60), new Vector3(12, 0, 60) };
        var s = new RagdollSolver(seeds, FloorTrace);
        s.AddStick(0, 1);
        s.SetVelocity(new Vector3(30f, 0, 0));

        for (int i = 0; i < 5 * 60 && !s.Sleeping; i++)
            s.Step(1f / 60f);

        Assert.True(s.Sleeping, "the body should settle and sleep well within 5 simulated seconds");
        for (int i = 0; i < s.ParticleCount; i++)
        {
            Assert.True(s.Position(i).Z >= s.Radius - 0.15f, $"particle {i} sank through the floor: {s.Position(i)}");
            Assert.Equal(s.Position(i), s.PositionLerped(i)); // slept: render read is the settled pose
        }
        // A slept solver is inert (zero cost, stable pose).
        Vector3 before = s.Position(0);
        s.Step(1f);
        Assert.Equal(before, s.Position(0));
    }

    [Fact]
    public void Step_DtSlicing_DoesNotChangeTheOutcome()
    {
        RagdollSolver Make()
        {
            var s = new RagdollSolver(new[] { new Vector3(0, 0, 300), new Vector3(0, 14, 300) }, FloorTrace);
            s.AddStick(0, 1);
            s.SetVelocity(new Vector3(75f, -20f, 10f));
            return s;
        }
        RagdollSolver a = Make(), b = Make();
        for (int i = 0; i < 30; i++) a.Step(1f / 30f);                       // 30 big frames
        for (int i = 0; i < 60; i++) b.Step(1f / 60f);                       // 60 small frames, same 1 s
        for (int i = 0; i < a.ParticleCount; i++)
        {
            float d = Vector3.Distance(a.Position(i), b.Position(i));
            Assert.True(d < 1e-3f, $"particle {i} diverged {d} between dt slicings");
        }
    }

    [Fact]
    public void ArcAlign_CarriesTheSeedBasis_AlongTheShortestArc()
    {
        // +Z → +X is a 90° turn about +Y: the identity basis' Fwd (+X) must land on −Z.
        BoneMatrix m = RagdollRig.ArcAlign(new Vector3(0, 0, 1), new Vector3(1, 0, 0),
            BoneMatrix.Identity, new Vector3(5, 6, 7));
        AssertVec(new Vector3(0, 0, -1), m.Fwd, 1e-4f);
        AssertVec(new Vector3(0, 1, 0), m.Left, 1e-4f);
        AssertVec(new Vector3(1, 0, 0), m.Up, 1e-4f);
        AssertVec(new Vector3(5, 6, 7), m.Origin, 0f);

        // Identical directions: unchanged basis.
        BoneMatrix id = RagdollRig.ArcAlign(new Vector3(0, 0, 1), new Vector3(0, 0, 1),
            BoneMatrix.Identity, Vector3.Zero);
        AssertVec(new Vector3(1, 0, 0), id.Fwd, 1e-5f);

        // Antiparallel: a clean 180° (no NaN), the segment axis itself must map through.
        BoneMatrix flip = RagdollRig.ArcAlign(new Vector3(0, 0, 1), new Vector3(0, 0, -1),
            BoneMatrix.Identity, Vector3.Zero);
        AssertVec(new Vector3(0, 0, -1), flip.Up, 1e-4f); // basis Up (+Z) follows the segment to −Z
        Assert.False(float.IsNaN(flip.Fwd.X));
    }

    // ------------------------------------------------------------------------------------------------
    //  Rig resolution
    // ------------------------------------------------------------------------------------------------

    private sealed class NamedRig : ISkeletalModel
    {
        private readonly (string Name, int Parent, Vector3 Rel)[] _bones;
        public NamedRig((string Name, int Parent, Vector3 Rel)[] bones) => _bones = bones;
        public int BoneCount => _bones.Length;
        public string BoneName(int b) => _bones[b].Name;
        public int BoneParent(int b) => _bones[b].Parent;
        public BoneMatrix BindRelative(int b)
            => BoneMatrix.FromTRS(_bones[b].Rel, Quaternion.Identity, Vector3.One);
        public int FrameCount => 1;
        public BoneMatrix FrameRelative(int frame, int b) => BindRelative(b);
    }

    /// <summary>The stock Xonotic player deform skeleton — names AND topology as actually shipped
    /// (erebus/gak/umbra/…): the deform leg chain ends at <c>lowerleg_*</c>; <c>foot_*</c>/<c>toe_*</c>
    /// hang under the IK roots <c>leg_*</c> (children of <c>master</c>), and <c>knee_*</c> are IK poles.</summary>
    private static (string, int, Vector3)[] StockBones() => new (string, int, Vector3)[]
    {
        ("master", -1, new(0, 0, 0)),        // 0
        ("spine1", 0, new(0, 0, 40)),        // 1  pelvis
        ("leg_L", 0, new(0, 6, 2)),          // 2  IK root, carries the foot
        ("leg_R", 0, new(0, -6, 2)),         // 3
        ("knee_L", 0, new(0, 6, 25)),        // 4  IK pole
        ("knee_R", 0, new(0, -6, 25)),       // 5
        ("hip_L", 1, new(0, 4, -6)),         // 6
        ("hip_R", 1, new(0, -4, -6)),        // 7
        ("spine2", 1, new(0, 0, 8)),         // 8
        ("foot_L", 2, new(0, 0, 0)),         // 9  under the IK root, NOT the lowerleg
        ("toe_L", 2, new(0, 4, -1)),         // 10
        ("foot_R", 3, new(0, 0, 0)),         // 11
        ("toe_R", 3, new(0, -4, -1)),        // 12
        ("upperleg_L", 6, new(0, 2, -2)),    // 13
        ("upperleg_R", 7, new(0, -2, -2)),   // 14
        ("spine3", 8, new(0, 0, 8)),         // 15
        ("lowerleg_L", 13, new(0, 0, -18)),  // 16
        ("lowerleg_R", 14, new(0, 0, -18)),  // 17
        ("spine4", 15, new(0, 0, 8)),        // 18  chest
        ("neck", 18, new(0, 0, 6)),          // 19
        ("shoulder_L", 18, new(0, 6, 2)),    // 20
        ("shoulder_R", 18, new(0, -6, 2)),   // 21
        ("head", 19, new(0, 0, 4)),          // 22
        ("upperarm_L", 20, new(0, 4, 0)),    // 23
        ("upperarm_R", 21, new(0, -4, 0)),   // 24
        ("forearm_L", 23, new(0, 12, 0)),    // 25
        ("forearm_R", 24, new(0, -12, 0)),   // 26
        ("hand_L", 25, new(0, 11, 0)),       // 27
        ("hand_R", 26, new(0, -11, 0)),      // 28
    };

    [Fact]
    public void TryBuild_ResolvesTheStockRig_AndDrivesBonesParentsFirst()
    {
        var model = new NamedRig(StockBones());
        var mgr = new SkeletonManager();
        int skel = mgr.Create(model);
        mgr.Build(skel, new SkeletonAnim(frame: 0), model, 0f, 1, model.BoneCount);

        RagdollRig? rig = RagdollRig.TryBuild(mgr, skel);
        Assert.NotNull(rig);
        rig!.CaptureSeed(bone => mgr.GetBoneAbs(skel, bone));
        foreach (Vector3 p in rig.SeedPositions)
            Assert.False(float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsNaN(p.Z));

        // Wire + settle a short drop, then drive: 10 bones written, parents before children.
        var solver = new RagdollSolver(rig.SeedPositions, FloorTrace);
        rig.Wire(solver);
        solver.SetVelocity(new Vector3(50, 0, 0));
        for (int i = 0; i < 90; i++) solver.Step(1f / 60f);

        var written = new List<int>();
        var particles = new Vector3[RagdollRig.ParticleCount];
        for (int i = 0; i < particles.Length; i++) particles[i] = solver.PositionLerped(i);
        rig.DriveBones(particles, (bone, abs) =>
        {
            written.Add(bone);
            Assert.False(float.IsNaN(abs.Origin.X), $"bone {bone} got a NaN origin");
        });
        // 12 driven bones (torso, head, 4 arm segments, 4 leg segments, 2 feet) + 2 toe companions.
        Assert.Equal(14, written.Count);
        for (int i = 1; i < 12; i++)
            Assert.True(written[i] > written[i - 1], "driven bones must be written parents-first (ascending ids)");
        // The toes resolved as companions (1-based bonenums for toe_L=10, toe_R=12 → 11/13).
        Assert.Contains(11, written);
        Assert.Contains(13, written);
    }

    [Fact]
    public void TryBuild_RefusesMissingBones_AndBrokenTopology()
    {
        // Rename a required bone (forearm_L, index 25) → no ragdoll.
        var missing = StockBones();
        missing[25] = ("fore_L", 23, missing[25].Item3);
        var mgr1 = new SkeletonManager();
        Assert.Null(RagdollRig.TryBuild(mgr1, mgr1.Create(new NamedRig(missing))));

        // Re-parent the forearm off the torso (not under the upperarm) → topology check refuses.
        var broken = StockBones();
        broken[25] = ("forearm_L", 1, broken[25].Item3);
        var mgr2 = new SkeletonManager();
        Assert.Null(RagdollRig.TryBuild(mgr2, mgr2.Create(new NamedRig(broken))));
    }

    [Fact]
    public void TryBuild_ResolvesAShippedPlayerModel()
    {
        if (!Directory.Exists(DataDir)) return;
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountGameDir(DataDir)) return;

        // Any stock player model should resolve; take the first with a real skeleton.
        foreach (string path in vfs.Find("models/player/", "iqm").Take(20))
        {
            var iqm = IqmReader.Read(vfs.ReadBytes(path));
            if (iqm.Joints.Length < 20)
                continue;
            var model = new IqmSkeletalModel(iqm);
            var mgr = new SkeletonManager();
            int skel = mgr.Create(model);
            mgr.Build(skel, new SkeletonAnim(frame: 0), model, 0f, 1, model.BoneCount);

            RagdollRig? rig = RagdollRig.TryBuild(mgr, skel);
            Assert.True(rig is not null, $"stock player rig failed to resolve: {path}");
            rig!.CaptureSeed(bone => mgr.GetBoneAbs(skel, bone));
            // The seeds must be a plausible body: distinct head/pelvis, left/right pairs apart.
            Assert.True(Vector3.Distance(rig.SeedPositions[0], rig.SeedPositions[2]) > 4f,
                $"head sits on the pelvis in {path}?");
            return; // one real model is enough
        }
    }

    private static void AssertVec(Vector3 expected, Vector3 actual, float tol = 1e-4f)
        => Assert.True((expected - actual).Length() <= tol, $"expected {expected}, got {actual} (tol {tol})");
}
