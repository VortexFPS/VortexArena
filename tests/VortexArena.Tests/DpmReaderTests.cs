using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using XonoticGodot.Formats;
using XonoticGodot.Formats.Dpm;
using XonoticGodot.Formats.Vfs;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T31 — direct coverage for <see cref="DpmReader"/> (port of Darkplaces <c>Mod_DARKPLACESMODEL_Load</c>,
/// model_alias.c:2163). The previously-untested facts pinned here: the 16-byte id
/// "DARKPLACESMODEL\0", type MUST be 2 (hierarchical skeletal), EVERY multi-byte field is BIG-endian,
/// all header/per-mesh/per-frame offsets are ABSOLUTE (file-relative), bones are 40 bytes
/// (name[32]+parent+flags) with parents strictly before children, frames carry numbones 3x4 float
/// matrices (48 bytes each), and vertices are variable-size weighted records walked sequentially.
/// Real-asset half (skip-if-missing) parses the shipped zombie.dpm once via a Lazy cache (3MB).
/// </summary>
public class DpmReaderTests
{
    private const string DataDir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";

    // ---------------------------------------------------------------- synthetic builder (big-endian!)

    private static void PutU32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o), v);
    private static void PutI32(byte[] b, int o, int v) => BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(o), v);
    private static void PutF(byte[] b, int o, float v) => BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(o), v);
    private static void PutStr(byte[] b, int o, string s) => Encoding.ASCII.GetBytes(s).CopyTo(b, o);

    private const int OfsBones = 80, OfsMeshes = 120, OfsFrames = 176;
    private const int OfsVerts = 244, OfsTex = 352, OfsIdx = 376, OfsGroups = 388, OfsPoses = 392;
    private const int FileSize = 440;

    /// <summary>1 bone, 1 mesh (3 single-weight vertices, 1 triangle), 1 frame. All offsets absolute.</summary>
    private static byte[] BuildSynthetic(
        string id = "DARKPLACESMODEL\0", uint type = 2, int boneParent = -1,
        uint triIndex2 = 2, uint weightBone = 0, uint numBones = 1)
    {
        var b = new byte[FileSize];

        PutStr(b, 0, id);
        PutU32(b, 16, type);
        PutU32(b, 20, FileSize);
        PutF(b, 24, -8f); PutF(b, 28, -8f); PutF(b, 32, -8f);  // mins
        PutF(b, 36, 8f); PutF(b, 40, 8f); PutF(b, 44, 8f);     // maxs
        PutF(b, 48, 12f);   // yawradius
        PutF(b, 52, 16f);   // allradius
        PutU32(b, 56, numBones);
        PutU32(b, 60, 1);   // num_meshs
        PutU32(b, 64, 1);   // num_frames
        PutU32(b, 68, OfsBones);
        PutU32(b, 72, OfsMeshes);
        PutU32(b, 76, OfsFrames);

        // bone[0]: name[32] + parent + flags
        PutStr(b, OfsBones, "root");
        PutI32(b, OfsBones + 32, boneParent);
        PutU32(b, OfsBones + 36, 1u);

        // mesh: shadername[32] + num_verts + num_tris + 4 absolute offsets
        PutStr(b, OfsMeshes, "skins/test");
        PutU32(b, OfsMeshes + 32, 3);
        PutU32(b, OfsMeshes + 36, 1);
        PutU32(b, OfsMeshes + 40, OfsVerts);
        PutU32(b, OfsMeshes + 44, OfsTex);
        PutU32(b, OfsMeshes + 48, OfsIdx);
        PutU32(b, OfsMeshes + 52, OfsGroups);

        // frame: name[32] + bounds(8f) + ofs_bonepositions (absolute)
        PutStr(b, OfsFrames, "idle");
        PutF(b, OfsFrames + 32, -8f); PutF(b, OfsFrames + 36, -8f); PutF(b, OfsFrames + 40, -8f);
        PutF(b, OfsFrames + 44, 8f); PutF(b, OfsFrames + 48, 8f); PutF(b, OfsFrames + 52, 8f);
        PutF(b, OfsFrames + 56, 12f); PutF(b, OfsFrames + 60, 16f);
        PutI32(b, OfsFrames + 64, OfsPoses);

        // 3 vertices, each: uint numbones=1 then one dpmbonevert_t (origin, influence, normal, bonenum)
        for (int v = 0; v < 3; v++)
        {
            int o = OfsVerts + v * 36;
            PutU32(b, o, 1);
            PutF(b, o + 4, v); PutF(b, o + 8, 2f * v); PutF(b, o + 12, 3f * v); // origin (bone-relative)
            PutF(b, o + 16, 1f);                                               // influence
            PutF(b, o + 20, 0f); PutF(b, o + 24, 0f); PutF(b, o + 28, 1f);     // normal
            PutU32(b, o + 32, weightBone);                                     // bonenum
        }

        // texcoords (2 floats each)
        for (int v = 0; v < 3; v++)
        {
            PutF(b, OfsTex + v * 8, 0.1f * v);
            PutF(b, OfsTex + v * 8 + 4, 0.2f * v);
        }

        // triangle indices (3 big-endian uints)
        PutU32(b, OfsIdx, 0); PutU32(b, OfsIdx + 4, 1); PutU32(b, OfsIdx + 8, triIndex2);
        PutU32(b, OfsGroups, 7); // groupid

        // bone pose: float[3][4] rows (basis columns + translation column)
        PutF(b, OfsPoses + 0, 1f); PutF(b, OfsPoses + 4, 0f); PutF(b, OfsPoses + 8, 0f); PutF(b, OfsPoses + 12, 10f);
        PutF(b, OfsPoses + 16, 0f); PutF(b, OfsPoses + 20, 1f); PutF(b, OfsPoses + 24, 0f); PutF(b, OfsPoses + 28, 20f);
        PutF(b, OfsPoses + 32, 0f); PutF(b, OfsPoses + 36, 0f); PutF(b, OfsPoses + 40, 1f); PutF(b, OfsPoses + 44, 30f);

        return b;
    }

    // ---------------------------------------------------------------- synthetic: layout facts

    [Fact]
    public void Synthetic_BigEndianHeaderAndBounds_Decode()
    {
        DpmData dpm = DpmReader.Read(BuildSynthetic());

        Assert.Equal(new Vector3(-8, -8, -8), dpm.Mins);
        Assert.Equal(new Vector3(8, 8, 8), dpm.Maxs);
        Assert.Equal(12f, dpm.YawRadius);
        Assert.Equal(16f, dpm.AllRadius);
        Assert.Single(dpm.Bones);
        Assert.Single(dpm.Meshes);
        Assert.Single(dpm.Frames);
        Assert.Equal("root", dpm.Bones[0].Name);
        Assert.Equal(-1, dpm.Bones[0].Parent);
    }

    [Fact]
    public void Synthetic_MeshVerticesTexcoordsIndices_Decode()
    {
        DpmData dpm = DpmReader.Read(BuildSynthetic());
        DpmMesh mesh = dpm.Meshes[0];

        Assert.Equal("skins/test", mesh.ShaderName);
        Assert.Equal(3, mesh.Vertices.Length);
        Assert.Equal(new[] { 0, 1, 2 }, mesh.Triangles);
        Assert.Equal(new Vector2(0.2f, 0.4f), mesh.TexCoords[2]);
        Assert.Equal(7u, mesh.GroupIds.Single());

        // bone-relative weighted vertex 1: origin (1,2,3), influence 1, normal +Z, bone 0
        var w = mesh.Vertices[1].Weights.Single();
        Assert.Equal(0, w.BoneNum);
        Assert.Equal(new Vector3(1, 2, 3), w.Origin);
        Assert.Equal(1f, w.Influence);
        Assert.Equal(new Vector3(0, 0, 1), w.Normal);
    }

    [Fact]
    public void Synthetic_FrameBonePose_DecodesColumns()
    {
        DpmData dpm = DpmReader.Read(BuildSynthetic());
        DpmFrame frame = dpm.Frames[0];

        Assert.Equal("idle", frame.Name);
        Assert.Single(frame.BonePoses);   // one pose per bone

        // matrix rows were (1,0,0,10),(0,1,0,20),(0,0,1,30): columns are the basis, col 3 the translation.
        DpmBonePose pose = frame.BonePoses[0];
        Assert.Equal(new Vector3(10, 20, 30), pose.Origin);
        Assert.Equal(new Vector3(1, 0, 0), pose.Right);
        Assert.Equal(new Vector3(0, 1, 0), pose.Up);
        Assert.Equal(new Vector3(0, 0, 1), pose.Forward);
    }

    // ---------------------------------------------------------------- synthetic: error paths

    [Fact]
    public void WrongId_Throws()
        => Assert.Throws<AssetParseException>(() => DpmReader.Read(BuildSynthetic(id: "DARKPLACESMODEX\0")));

    [Fact]
    public void NonSkeletalType_Throws()
    {
        // Only type 2 (hierarchical skeletal pose) is supported, exactly like DP.
        Assert.Throws<AssetParseException>(() => DpmReader.Read(BuildSynthetic(type: 1)));
        Assert.Throws<AssetParseException>(() => DpmReader.Read(BuildSynthetic(type: 3)));
    }

    [Fact]
    public void TooSmall_Throws()
        => Assert.Throws<AssetParseException>(() => DpmReader.Read(new byte[79]));

    [Fact]
    public void BoneParentNotBeforeChild_Throws()
        // bone[0] claiming parent 0 (itself) violates "parents come first" — DP hard-errors.
        => Assert.Throws<AssetParseException>(() => DpmReader.Read(BuildSynthetic(boneParent: 0)));

    [Fact]
    public void BoneParentBelowMinusOne_Throws()
        => Assert.Throws<AssetParseException>(() => DpmReader.Read(BuildSynthetic(boneParent: -2)));

    [Fact]
    public void TriangleIndexOutOfRange_Throws()
        => Assert.Throws<AssetParseException>(() => DpmReader.Read(BuildSynthetic(triIndex2: 3)));

    [Fact]
    public void WeightBoneOutOfRange_Throws()
        => Assert.Throws<AssetParseException>(() => DpmReader.Read(BuildSynthetic(weightBone: 5)));

    [Fact]
    public void ZeroBones_Throws()
        => Assert.Throws<AssetParseException>(() => DpmReader.Read(BuildSynthetic(numBones: 0)));

    // ---------------------------------------------------------------- real asset (skip-if-missing)

    // zombie.dpm is ~3MB; parse it once for the whole class via a Lazy cache (serial suite stays fast).
    private static readonly Lazy<DpmData?> RealDpm = new(() =>
    {
        if (!Directory.Exists(DataDir)) return null;
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountGameDir(DataDir)) return null;
        string? path = vfs.Find("models/", "dpm").FirstOrDefault(p => p.EndsWith("zombie.dpm", StringComparison.OrdinalIgnoreCase))
                       ?? vfs.Find("models/", "dpm").FirstOrDefault();
        return path is null ? null : DpmReader.Read(vfs.ReadBytes(path));
    });

    [Fact]
    public void RealAsset_SkeletonInvariants()
    {
        DpmData? dpm = RealDpm.Value;
        if (dpm is null) return; // skip-if-missing

        Assert.True(dpm.Bones.Length >= 1);
        Assert.True(dpm.Frames.Length >= 1);
        // Parents strictly precede children (the invariant the forward-pass composer relies on).
        for (int i = 0; i < dpm.Bones.Length; i++)
            Assert.InRange(dpm.Bones[i].Parent, -1, i - 1);
        // Every frame carries one pose per bone.
        foreach (DpmFrame f in dpm.Frames)
            Assert.Equal(dpm.Bones.Length, f.BonePoses.Length);
    }

    [Fact]
    public void RealAsset_MeshInvariants()
    {
        DpmData? dpm = RealDpm.Value;
        if (dpm is null) return; // skip-if-missing

        Assert.True(dpm.Meshes.Length >= 1);
        foreach (DpmMesh m in dpm.Meshes)
        {
            Assert.True(m.Vertices.Length > 0);
            Assert.Equal(m.Vertices.Length, m.TexCoords.Length);
            Assert.True(m.Triangles.Length > 0 && m.Triangles.Length % 3 == 0);
            Assert.Equal(m.Triangles.Length / 3, m.GroupIds.Length);
            Assert.All(m.Triangles, i => Assert.InRange(i, 0, m.Vertices.Length - 1));
            foreach (DpmVertex v in m.Vertices)
            {
                Assert.True(v.Weights.Length >= 1);
                foreach (DpmBoneWeight w in v.Weights)
                    Assert.InRange(w.BoneNum, 0, dpm.Bones.Length - 1);
            }
        }
    }
}
