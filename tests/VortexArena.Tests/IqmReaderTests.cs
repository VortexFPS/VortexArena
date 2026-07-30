using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using XonoticGodot.Formats;
using XonoticGodot.Formats.Iqm;
using XonoticGodot.Formats.Vfs;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T31 — direct coverage for <see cref="IqmReader"/> (port of Darkplaces <c>Mod_INTERQUAKEMODEL_Load</c>,
/// model_alias.c:3219). Synthetic buffers pin the header layout (16-byte magic "INTERQUAKEMODEL\0",
/// 124-byte header, version 1 OR 2, file-relative little-endian section offsets), the v1
/// quaternion-w reconstruction (<c>w = -sqrt(max(0, 1 - x²-y²-z²))</c>, the negative hemisphere DP
/// uses), the joints-parents-first invariant, and the section bound checks. Real-asset half
/// (skip-if-missing) checks shipped .iqm structural invariants.
/// </summary>
public class IqmReaderTests
{
    private const string DataDir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";

    private const int HeaderSize = 124;
    private const int Joint2Size = 48;
    private const int Joint1Size = 44;

    private static void PutU32(byte[] b, int o, uint v) => BitConverter.GetBytes(v).CopyTo(b, o);
    private static void PutI32(byte[] b, int o, int v) => BitConverter.GetBytes(v).CopyTo(b, o);
    private static void PutF(byte[] b, int o, float v) => BitConverter.GetBytes(v).CopyTo(b, o);

    /// <summary>
    /// Minimal joints-only IQM: no vertices/meshes/anims, 1 joint at offset 124. Version selects the
    /// 44-byte (v1, quat xyz only) or 48-byte (v2, full quat) joint record.
    /// </summary>
    private static byte[] BuildJointOnly(int version, int jointParent,
        float qx, float qy, float qz, float qw = 0f, uint numTriangles = 0, uint ofsTriangles = 0)
    {
        int jointSize = version == 1 ? Joint1Size : Joint2Size;
        var b = new byte[HeaderSize + jointSize];
        Encoding.ASCII.GetBytes("INTERQUAKEMODEL\0").CopyTo(b, 0);

        int o = 16;
        PutU32(b, o + 0, (uint)version);
        PutU32(b, o + 4, (uint)b.Length);   // filesize
        PutU32(b, o + 8, 0);                // flags
        // num_text/ofs_text = 0,0 (o+12, o+16) ... most fields stay zero.
        PutU32(b, o + 40, numTriangles);    // num_triangles
        PutU32(b, o + 44, ofsTriangles);    // ofs_triangles
        PutU32(b, o + 52, 1);               // num_joints
        PutU32(b, o + 56, HeaderSize);      // ofs_joints

        int j = HeaderSize;
        PutU32(b, j + 0, 0);                // name offset (empty text blob -> "")
        PutI32(b, j + 4, jointParent);
        PutF(b, j + 8, 1f); PutF(b, j + 12, 2f); PutF(b, j + 16, 3f);  // translate
        if (version == 1)
        {
            PutF(b, j + 20, qx); PutF(b, j + 24, qy); PutF(b, j + 28, qz);
            PutF(b, j + 32, 1f); PutF(b, j + 36, 1f); PutF(b, j + 40, 1f); // scale
        }
        else
        {
            PutF(b, j + 20, qx); PutF(b, j + 24, qy); PutF(b, j + 28, qz); PutF(b, j + 32, qw);
            PutF(b, j + 36, 1f); PutF(b, j + 40, 1f); PutF(b, j + 44, 1f); // scale
        }
        return b;
    }

    // ---------------------------------------------------------------- synthetic: layout + quat facts

    [Fact]
    public void V2_JointParses_TranslateScaleParent()
    {
        IqmData iqm = IqmReader.Read(BuildJointOnly(version: 2, jointParent: -1, qx: 0, qy: 0, qz: 0, qw: 1f));

        Assert.Equal(2, iqm.Version);
        IqmJoint joint = iqm.Joints.Single();
        Assert.Equal(-1, joint.Parent);
        Assert.Equal(new Vector3(1, 2, 3), joint.Translate);
        Assert.Equal(new Vector3(1, 1, 1), joint.Scale);
    }

    [Fact]
    public void V2_JointQuaternion_NormalizedToNonPositiveW()
    {
        // DP forces the scalar part non-positive (sign-flips the quat, same rotation) then normalizes.
        IqmData iqm = IqmReader.Read(BuildJointOnly(version: 2, jointParent: -1, qx: 0, qy: 0, qz: 0, qw: 1f));
        Quaternion q = iqm.Joints[0].Rotate;
        Assert.True(q.W <= 0f, $"expected non-positive w, got {q.W}");
        Assert.Equal(1f, q.Length(), 4);
    }

    [Fact]
    public void V1_JointQuaternion_WReconstructedNegative()
    {
        // v1 stores only quat xyz; DP reconstructs w = -sqrt(max(0, 1 - x²-y²-z²)).
        IqmData identity = IqmReader.Read(BuildJointOnly(version: 1, jointParent: -1, qx: 0, qy: 0, qz: 0));
        Assert.Equal(1, identity.Version);
        Assert.Equal(-1f, identity.Joints[0].Rotate.W, 5);

        IqmData half = IqmReader.Read(BuildJointOnly(version: 1, jointParent: -1, qx: 0.6f, qy: 0f, qz: 0.8f));
        Assert.Equal(0f, half.Joints[0].Rotate.W, 4);     // 1 - 0.36 - 0.64 = 0
        Assert.Equal(0.6f, half.Joints[0].Rotate.X, 5);
        Assert.Equal(0.8f, half.Joints[0].Rotate.Z, 5);
    }

    // ---------------------------------------------------------------- synthetic: error paths

    [Fact]
    public void WrongMagic_Throws()
    {
        byte[] b = BuildJointOnly(2, -1, 0, 0, 0, 1f);
        b[0] = (byte)'X';
        Assert.Throws<AssetParseException>(() => IqmReader.Read(b));
    }

    [Fact]
    public void UnsupportedVersion_Throws()
    {
        // Only versions 1 and 2 are accepted (DP checks the same).
        Assert.Throws<AssetParseException>(() => IqmReader.Read(BuildJointOnly(version: 3, jointParent: -1, 0, 0, 0, 1f)));
    }

    [Fact]
    public void TooSmall_Throws()
        => Assert.Throws<AssetParseException>(() => IqmReader.Read(new byte[HeaderSize - 1]));

    [Fact]
    public void JointParentNotBeforeChild_Throws()
        // joint[0] with parent 0 (itself) — parents must strictly precede children.
        => Assert.Throws<AssetParseException>(() => IqmReader.Read(BuildJointOnly(version: 2, jointParent: 0, 0, 0, 0, 1f)));

    [Fact]
    public void SectionOutOfBounds_Throws()
        // a triangles section that lies past the end of the buffer is rejected up front.
        => Assert.Throws<AssetParseException>(() => IqmReader.Read(
            BuildJointOnly(version: 2, jointParent: -1, 0, 0, 0, 1f, numTriangles: 10, ofsTriangles: 100000)));

    // ---------------------------------------------------------------- real assets (skip-if-missing)

    private static readonly Lazy<IqmData?> RealIqm = new(() =>
    {
        if (!Directory.Exists(DataDir)) return null;
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountGameDir(DataDir)) return null;
        string? path = vfs.Find("models/", "iqm").FirstOrDefault();
        return path is null ? null : IqmReader.Read(vfs.ReadBytes(path));
    });

    [Fact]
    public void RealAsset_RequiredVertexArrays_AndTriangleBounds()
    {
        IqmData? iqm = RealIqm.Value;
        if (iqm is null) return; // skip-if-missing

        Assert.True(iqm.Version is 1 or 2);
        // POSITION float3 + TEXCOORD float2 are mandatory whenever the model has vertices (DP hard-errors).
        Assert.True(iqm.VertexCount > 0);
        Assert.Equal(iqm.VertexCount, iqm.Positions.Length);
        Assert.Equal(iqm.VertexCount, iqm.TexCoords.Length);
        if (iqm.Normals is not null) Assert.Equal(iqm.VertexCount, iqm.Normals.Length);

        Assert.True(iqm.Triangles.Length % 3 == 0);
        Assert.All(iqm.Triangles, i => Assert.InRange(i, 0, iqm.VertexCount - 1));

        foreach (IqmMesh m in iqm.Meshes)
        {
            Assert.True(m.FirstVertex + m.VertexCount <= iqm.VertexCount);
            Assert.True((m.FirstTriangle + m.TriangleCount) * 3 <= iqm.Triangles.Length);
        }
    }

    [Fact]
    public void RealAsset_SkeletonAndAnimationInvariants()
    {
        IqmData? iqm = RealIqm.Value;
        if (iqm is null) return; // skip-if-missing

        for (int i = 0; i < iqm.Joints.Length; i++)
        {
            Assert.InRange(iqm.Joints[i].Parent, -1, i - 1);
            // joint rest rotations come out unit-length in DP's non-positive-w hemisphere
            Assert.Equal(1f, iqm.Joints[i].Rotate.Length(), 3);
            Assert.True(iqm.Joints[i].Rotate.W <= 1e-4f);
        }

        // An animated model decodes one frame per num_frames, each with one bone pose per pose channel set.
        foreach (IqmFrame f in iqm.Frames)
            Assert.Equal(iqm.Poses.Length, f.Bones.Length);
        foreach (IqmAnim a in iqm.Anims)
            Assert.True(a.FirstFrame + a.FrameCount <= iqm.Frames.Length,
                $"anim '{a.Name}' frame range exceeds decoded frames");
        if (iqm.Bounds is not null)
            Assert.Equal(iqm.Frames.Length, iqm.Bounds.Length);
        // Skinning arrays are mandatory for an animated model (DP hard requirement). They are FLAT
        // vertex*4 ubyte layouts (see IqmData), so length pins both presence and shape.
        if (iqm.Frames.Length > 0 || iqm.Anims.Length > 0)
        {
            Assert.NotNull(iqm.BlendIndexes);
            Assert.NotNull(iqm.BlendWeights);
            Assert.Equal(iqm.VertexCount * 4, iqm.BlendIndexes!.Length);
            Assert.Equal(iqm.VertexCount * 4, iqm.BlendWeights!.Length);
        }
    }
}
