using System;
using System.Linq;
using System.Numerics;
using VortexArena.Formats.Md3;
using VortexArena.Formats.Mdl;
using VortexArena.Legacy.Csqc;
using VortexArena.Legacy.Presentation;
using VortexArena.QuakeC;
using Xunit;

namespace VortexArena.Tests.Legacy;

/// <summary>
/// What a legacy session tells the client program and its own scene about a Quake model or sprite
/// (<see cref="LegacyModelLoader"/>), and the rules DarkPlaces applies to an entity the SERVER networks:
/// which poses it is drawn in (<see cref="LegacyNetworkEntityAnimation"/>, cl_main.c CL_UpdateNetworkEntity
/// and prvm_cmds.c VM_FrameBlendFromFrameGroupBlend), its trail (<see cref="LegacyNetworkEntityTrail"/>) and
/// its skin (<see cref="MdlRender"/>). All synthetic.
/// </summary>
public class QuakeModelPresentationTests
{
    private static LegacyModel SampleModel() =>
        LegacyModelLoader.Load("progs/sample.mdl", QuakeModelFormatTests.Sample().Build(), null)!;

    [Fact]
    public void LegacyModel_OfAnMdl_HasFrameGroupsAsFrames_TheUsedBox_AndTheModelsEffects()
    {
        LegacyModel m = SampleModel();
        Assert.Equal(LegacyModelKind.Alias, m.Kind);
        Assert.Equal("mdl", m.Format);
        // Three frame numbers over four poses: .frame 1 is the two-pose group at 20 poses a second.
        Assert.Equal(3, m.NumFrames);
        Assert.Equal(4, m.NumPoses);
        Assert.Equal(new LegacyAnimScene("flameA", 1, 2, 20f, true), m.Scenes![1]);
        Assert.Equal(new LegacyAnimScene("sixteencharsname", 3, 1, 10f, true), m.Scenes[2]);
        // The box of the vertices a triangle uses (the unused one at 250 250 250 is not in it).
        Assert.Equal(new Vector3(-3f, 4f, -8f), m.NormalMins);
        Assert.Equal(new Vector3(2f, 9f, 0f), m.NormalMaxs);
        Assert.Equal(MdlFlags.EfRocket | MdlFlags.EfRotate | 0x200u, m.Effects);
        Assert.Equal(2, m.SkinCount);
        Assert.True(m.Radius > m.YawRadius && m.YawRadius > 0);
    }

    [Fact]
    public void LegacyModel_OfASprite_HasOneFrameNumberPerSlot()
    {
        byte[] bytes = QuakeModelFormatTests.Sprite(1, 2, 0,
            (null, new[] { (-8, 8, 16, 16, new byte[256]) }),
            (new[] { 0.05f, 1f }, new[] { (-1, 1, 2, 2, new byte[4]), (-1, 1, 2, 2, new byte[4]) }));
        LegacyModel m = LegacyModelLoader.Load("progs/s.spr", bytes, null)!;
        Assert.Equal(LegacyModelKind.Sprite, m.Kind);
        Assert.Equal(2, m.NumFrames);
        Assert.Equal(3, m.NumPoses);
        Assert.Equal(new LegacyAnimScene("frame 1", 1, 2, 20f, true), m.Scenes![1]);
        float radius = MathF.Sqrt(128f);
        Assert.Equal(new Vector3(-radius), m.NormalMins);
        Assert.Equal(new Vector3(radius), m.NormalMaxs);
        Assert.Equal(0u, m.Effects);
    }

    [Fact]
    public void LegacyModel_FrameGroupsFile_KeepsTheModelsEffects()
    {
        LegacyModel m = LegacyModelLoader.Load("progs/sample.mdl", QuakeModelFormatTests.Sample().Build(), "0 2 5 1 walk\n")!;
        Assert.Single(m.Scenes!);
        Assert.Equal(MdlFlags.EfRocket | MdlFlags.EfRotate | 0x200u, m.Effects);
        Assert.Equal(2, m.SkinCount);
    }

    // ---- the frame lerp of a network entity ---------------------------------------------------------

    [Fact]
    public void NetworkAnimation_NewEntity_ShowsItsFrameWithNothingToBlendFrom()
    {
        LegacyModel m = SampleModel();
        var a = new LegacyNetworkEntityAnimation();
        a.Reset(2, time: 10.0);
        a.Update(m, 2, 10.0, 0.05);
        Assert.True(a.Poses(m, 10.0, false, out int poseA, out int poseB, out float lerp));
        Assert.Equal(3, poseA);        // frame 2 is pose 3
        Assert.Equal(3, poseB);
        Assert.Equal(0f, lerp);
    }

    [Fact]
    public void NetworkAnimation_AFrameChange_BlendsOverTheTimeThePreviousFrameLasted_AtMostATenth()
    {
        LegacyModel m = SampleModel();
        var a = new LegacyNetworkEntityAnimation();
        a.Reset(0, 10.0);
        a.Update(m, 0, 10.0, 0.05);
        // The server sets frame 2 at 10.05: "begin a new frame lerp".
        a.Update(m, 2, 10.05, 0.05);
        a.Poses(m, 10.05, false, out int poseA, out int poseB, out float lerp);
        Assert.Equal((0, 0f), (poseA, lerp));       // all of the old frame at the moment of the change
        // Half way through the 0.05 s the old frame lasted.
        a.Update(m, 2, 10.075, 0.05);
        a.Poses(m, 10.075, false, out poseA, out poseB, out lerp);
        Assert.Equal(new[] { 0, 3 }, new[] { poseA, poseB }.OrderBy(x => x));
        Assert.Equal(0.5f, poseA == 0 ? lerp : 1 - lerp, 3);
        // Past it: all of the new frame.
        a.Update(m, 2, 10.2, 0.05);
        a.Poses(m, 10.2, false, out poseA, out poseB, out lerp);
        Assert.Equal(3, poseA);
        Assert.Equal(0f, lerp);

        // A frame that lasted a second is still blended over a tenth (cl_lerpanim_maxdelta_server).
        a.Update(m, 0, 11.2, 0.05);
        a.Update(m, 0, 11.25, 0.05);
        a.Poses(m, 11.25, false, out poseA, out poseB, out lerp);
        Assert.Equal(0.5f, poseA == 0 ? 1 - lerp : lerp, 3);
    }

    [Fact]
    public void NetworkAnimation_AFrameGroup_PlaysByTheClock()
    {
        LegacyModel m = SampleModel();
        var a = new LegacyNetworkEntityAnimation();
        a.Reset(1, 100.0);                          // the two-pose group, 20 poses a second
        a.Update(m, 1, 100.0, 0.05);
        a.Poses(m, 100.0, false, out int poseA, out int poseB, out float lerp);
        Assert.Equal((1, 0f), (poseA, lerp));
        a.Update(m, 1, 100.025, 0.05);              // half a pose later: pose 1 and pose 2, evenly
        a.Poses(m, 100.025, false, out poseA, out poseB, out lerp);
        Assert.Equal(new[] { 1, 2 }, new[] { poseA, poseB }.OrderBy(x => x));
        Assert.Equal(0.5f, lerp, 3);
        a.Update(m, 1, 100.05, 0.05);               // one pose later
        a.Poses(m, 100.05, false, out poseA, out _, out lerp);
        Assert.Equal((2, 0f), (poseA, lerp));
        a.Update(m, 1, 100.1, 0.05);                // it loops
        a.Poses(m, 100.1, false, out poseA, out _, out lerp);
        Assert.Equal((1, 0f), (poseA, lerp));

        // r_lerpmodels 0: whole poses only.
        a.Poses(m, 100.025, noLerp: true, out poseA, out poseB, out lerp);
        Assert.Equal((1, 1, 0f), (poseA, poseB, lerp));
    }

    [Fact]
    public void NetworkAnimation_RestartAndTeleport()
    {
        LegacyModel m = SampleModel();
        var a = new LegacyNetworkEntityAnimation();
        a.Reset(0, 5.0);
        a.Update(m, 0, 5.0, 0.05);
        a.Update(m, 2, 5.5, 0.05);
        // EF_TELEPORT_BIT toggled: no blend at all.
        a.Reset(2, 5.52);
        a.Update(m, 2, 5.52, 0.05);
        a.Poses(m, 5.52, false, out int poseA, out _, out float lerp);
        Assert.Equal((3, 0f), (poseA, lerp));
        // EF_RESTARTANIM_BIT toggled with the same frame number: a new blend begins from what was shown.
        a.Restart(2, 6.0);
        a.Update(m, 2, 6.0, 0.05);
        a.Poses(m, 6.0, false, out poseA, out int poseB, out lerp);
        Assert.Equal((3, 3), (poseA, poseB));
    }

    [Fact]
    public void FrameBlend_OutOfRangeFrameIsFrameZero_AndNoModelIsOnePose()
    {
        LegacyModel m = SampleModel();
        Span<int> poses = stackalloc int[LegacyFrameBlend.MaxBlends];
        Span<float> weights = stackalloc float[LegacyFrameBlend.MaxBlends];
        int n = LegacyFrameBlend.FromGroups(m, new[] { new LegacyFrameGroupBlend(99, 1, 0) }, 3.0, false, poses, weights);
        Assert.Equal(1, n);
        Assert.Equal((0, 1f), (poses[0], weights[0]));
        n = LegacyFrameBlend.FromGroups(null, new[] { new LegacyFrameGroupBlend(5, 1, 0) }, 3.0, false, poses, weights);
        Assert.Equal((1, 0, 1f), (n, poses[0], weights[0]));
        // Two groups naming the same pose add up in one entry.
        n = LegacyFrameBlend.FromGroups(m, new[] { new LegacyFrameGroupBlend(0, 0.25f, 0), new LegacyFrameGroupBlend(0, 0.75f, 0) }, 3.0, false, poses, weights);
        Assert.Equal((1, 0, 1f), (n, poses[0], weights[0]));
    }

    // ---- trails ------------------------------------------------------------------------------------

    [Fact]
    public void Trail_IsNotLeftOnTheFirstFrame_NorAcrossAReset()
    {
        var t = new LegacyNetworkEntityTrail();
        Assert.False(t.Step(new QcVector(0, 0, 0), true, out _));               // first sight: only recorded
        Assert.True(t.Step(new QcVector(10, 0, 0), true, out QcVector from));
        Assert.Equal(new QcVector(0, 0, 0), from);
        Assert.False(t.Step(new QcVector(20, 0, 0), false, out _));             // no trail wanted, still recorded
        Assert.True(t.Step(new QcVector(30, 0, 0), true, out from));
        Assert.Equal(new QcVector(20, 0, 0), from);
        t.Reset();                                                               // teleported
        Assert.False(t.Step(new QcVector(5000, 0, 0), true, out _));
        Assert.True(t.Step(new QcVector(5010, 0, 0), true, out from));
        Assert.Equal(new QcVector(5000, 0, 0), from);
    }

    // ---- skins and the morph mesh --------------------------------------------------------------------

    [Fact]
    public void SkinPicture_FollowsTheSkinGroupsClock_AndOutOfRangeIsSkinZero()
    {
        MdlData mdl = MdlReader.Read(QuakeModelFormatTests.Sample().Build());
        // Skin 0: picture 0. Skin 1: pictures 1..3 at four a second.
        Assert.Equal(0, MdlRender.SkinPicture(mdl.SkinScenes, 0, 123.0));
        Assert.Equal(1, MdlRender.SkinPicture(mdl.SkinScenes, 1, 0.0));
        Assert.Equal(1, MdlRender.SkinPicture(mdl.SkinScenes, 1, 0.24));
        Assert.Equal(2, MdlRender.SkinPicture(mdl.SkinScenes, 1, 0.25));
        Assert.Equal(3, MdlRender.SkinPicture(mdl.SkinScenes, 1, 0.5));
        Assert.Equal(1, MdlRender.SkinPicture(mdl.SkinScenes, 1, 0.75));
        Assert.Equal(1, MdlRender.SkinPicture(mdl.SkinScenes, 1, -3.0));        // before the clock began
        Assert.Equal(0, MdlRender.SkinPicture(mdl.SkinScenes, 7, 0.0));          // "if (skinnum >= numskins) skinnum = 0"
        Assert.Equal(0, MdlRender.SkinPicture(mdl.SkinScenes, -1, 0.0));
        Assert.Equal(-1, MdlRender.SkinPicture(ReadOnlySpan<MdlScene>.Empty, 0, 0.0));
    }

    [Fact]
    public void ToMd3_IsTheCompactedMesh_WithEveryPose()
    {
        MdlData mdl = MdlReader.Read(QuakeModelFormatTests.Sample().Build());
        Md3Data md3 = MdlRender.ToMd3(mdl);
        Assert.Equal(4, md3.FrameCount);
        Md3Surface s = Assert.Single(md3.Surfaces);
        Assert.Equal("default", s.Name);
        Assert.Equal(5, s.VertexCount);
        Assert.Equal(mdl.MeshElements, s.Triangles);
        Assert.Equal(mdl.MeshTexCoords, s.TexCoords);
        Assert.Equal(4, s.FrameVertices.Length);
        // Mesh vertex 3 is the seam copy of file vertex 1: same position, its own texture coordinate.
        Assert.Equal(s.FrameVertices[2][1].Position, s.FrameVertices[2][3].Position);
        Assert.Equal(mdl.Frames[2].Vertices[1].Position, s.FrameVertices[2][3].Position);
        Assert.NotEqual(s.TexCoords[1], s.TexCoords[3]);
        Assert.Equal(mdl.Frames[3].Vertices[3].Position, s.FrameVertices[3][4].Position);
    }
}
