using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using XonoticGodot.Formats;
using XonoticGodot.Formats.Md3;
using XonoticGodot.Formats.Vfs;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T31 — direct coverage for <see cref="Md3Reader"/> (port of Darkplaces <c>Mod_IDP3_Load</c>,
/// model_alias.c:1578). Synthetic in-memory buffers pin the on-disk layout facts (magic "IDP3", version
/// MUST be 15, 108-byte header, 56-byte frameinfo, 112-byte FRAME-MAJOR tags, the mesh sub-header's own
/// "IDP3" ident with mesh-relative lump offsets, 8-byte vertices scaled by 1/64 with lat/long byte
/// normals) and the error paths; the real-asset half (skip-if-missing) checks structural invariants on
/// shipped Xonotic .md3 models. MD3 normals are compared with tolerance, never bit-exactly — the port
/// decodes via MathF where DP uses its mod_md3_sin lookup table.
/// </summary>
public class Md3ReaderTests
{
    private const string DataDir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";

    // ---------------------------------------------------------------- synthetic buffer builder

    private const int HeaderSize = 108;
    private const int FrameSize = 56;
    private const int TagSize = 112;
    private const int MeshHeaderSize = 108;
    private const int ShaderSize = 68;

    private static void PutInt(byte[] b, int o, int v) => BitConverter.GetBytes(v).CopyTo(b, o);
    private static void PutFloat(byte[] b, int o, float v) => BitConverter.GetBytes(v).CopyTo(b, o);
    private static void PutShort(byte[] b, int o, short v) => BitConverter.GetBytes(v).CopyTo(b, o);
    private static void PutStr(byte[] b, int o, string s) => Encoding.ASCII.GetBytes(s).CopyTo(b, o);

    /// <summary>
    /// Builds a well-formed MD3: 2 frames, 2 tags, 1 mesh (3 vertices, 1 triangle). Tag origins encode
    /// (frame, tagIndex, 0) so the frame-major on-disk order (all tags of frame 0, then frame 1) is
    /// verifiable after the reader pivots to tag-major.
    /// </summary>
    private static byte[] BuildSynthetic(
        int version = 15, string magic = "IDP3", string meshMagic = "IDP3",
        int meshFrames = 2, int triIndex2 = 2)
    {
        const int numFrames = 2, numTags = 2, numMeshes = 1, numVerts = 3, numTris = 1;
        int lumpFrames = HeaderSize;                                  // 108
        int lumpTags = lumpFrames + numFrames * FrameSize;            // 220
        int lumpMeshes = lumpTags + numFrames * numTags * TagSize;    // 668

        // mesh-relative lump offsets
        int mShaders = MeshHeaderSize;                                // 108
        int mTris = mShaders + 1 * ShaderSize;                        // 176
        int mTex = mTris + numTris * 12;                              // 188
        int mVerts = mTex + numVerts * 8;                             // 212
        int mEnd = mVerts + meshFrames * numVerts * 8;                // 260 (when meshFrames == 2... 212+48)

        var b = new byte[lumpMeshes + mEnd];

        PutStr(b, 0, magic);
        PutInt(b, 4, version);
        PutStr(b, 8, "synthetic");        // name[64]
        PutInt(b, 72, 0);                 // flags
        PutInt(b, 76, numFrames);
        PutInt(b, 80, numTags);
        PutInt(b, 84, numMeshes);
        PutInt(b, 88, 0);                 // num_skins
        PutInt(b, 92, lumpFrames);
        PutInt(b, 96, lumpTags);
        PutInt(b, 100, lumpMeshes);
        PutInt(b, 104, b.Length);         // lump_end

        // frames: mins/maxs/origin/radius/name[16]
        for (int f = 0; f < numFrames; f++)
        {
            int o = lumpFrames + f * FrameSize;
            PutFloat(b, o + 0, -1f); PutFloat(b, o + 4, -2f); PutFloat(b, o + 8, -3f);
            PutFloat(b, o + 12, 1f); PutFloat(b, o + 16, 2f); PutFloat(b, o + 20, 3f);
            PutFloat(b, o + 24, 0f); PutFloat(b, o + 28, 0f); PutFloat(b, o + 32, 0f);
            PutFloat(b, o + 36, 10f + f);
            PutStr(b, o + 40, "frame" + f);
        }

        // tags, FRAME-MAJOR: (f0,t0) (f0,t1) (f1,t0) (f1,t1); origin = (frame, tag, 0); identity axes.
        for (int f = 0; f < numFrames; f++)
        for (int t = 0; t < numTags; t++)
        {
            int o = lumpTags + (f * numTags + t) * TagSize;
            PutStr(b, o, t == 0 ? "tag_one" : "tag_two");
            PutFloat(b, o + 64, f); PutFloat(b, o + 68, t); PutFloat(b, o + 72, 0f);
            PutFloat(b, o + 76, 1f); PutFloat(b, o + 80, 0f); PutFloat(b, o + 84, 0f);  // axis X
            PutFloat(b, o + 88, 0f); PutFloat(b, o + 92, 1f); PutFloat(b, o + 96, 0f);  // axis Y
            PutFloat(b, o + 100, 0f); PutFloat(b, o + 104, 0f); PutFloat(b, o + 108, 1f); // axis Z
        }

        // mesh sub-header (own "IDP3" ident, NO version field) + lumps relative to the mesh start.
        int m = lumpMeshes;
        PutStr(b, m, meshMagic);
        PutStr(b, m + 4, "surface0");
        PutInt(b, m + 68, 0);             // flags
        PutInt(b, m + 72, meshFrames);    // must equal model frame count
        PutInt(b, m + 76, 1);             // num_shaders
        PutInt(b, m + 80, numVerts);
        PutInt(b, m + 84, numTris);
        PutInt(b, m + 88, mTris);         // lump_elements
        PutInt(b, m + 92, mShaders);      // lump_shaders
        PutInt(b, m + 96, mTex);          // lump_texcoords
        PutInt(b, m + 100, mVerts);       // lump_framevertices
        PutInt(b, m + 104, mEnd);         // lump_end (mesh-relative)

        PutStr(b, m + mShaders, "textures/test_shader");
        // triangle 0: indices 0,1,triIndex2 (triIndex2=2 valid; out-of-range to test the bound check)
        PutInt(b, m + mTris, 0); PutInt(b, m + mTris + 4, 1); PutInt(b, m + mTris + 8, triIndex2);
        for (int v = 0; v < numVerts; v++)
        {
            PutFloat(b, m + mTex + v * 8, 0.25f * v);
            PutFloat(b, m + mTex + v * 8 + 4, 0.5f * v);
        }
        // frame vertices: frame-major blocks. Position (64,128,-64) short = (1,2,-1) after the 1/64 scale.
        // Normals: v0 pitch=0   -> (0,0,1); v1 pitch=64,yaw=0 -> (1,0,0); v2 pitch=64,yaw=64 -> (0,1,0).
        for (int f = 0; f < meshFrames; f++)
        for (int v = 0; v < numVerts; v++)
        {
            int o = m + mVerts + (f * numVerts + v) * 8;
            PutShort(b, o, 64); PutShort(b, o + 2, 128); PutShort(b, o + 4, -64);
            b[o + 6] = (byte)(v == 0 ? 0 : 64);   // pitch (lat)
            b[o + 7] = (byte)(v == 2 ? 64 : 0);   // yaw (long)
        }

        return b;
    }

    // ---------------------------------------------------------------- synthetic: layout facts

    [Fact]
    public void Synthetic_ParsesHeader_Frames_AndShaders()
    {
        Md3Data md3 = Md3Reader.Read(BuildSynthetic());

        Assert.Equal("synthetic", md3.Name);
        Assert.Equal(2, md3.FrameCount);
        Assert.Equal(2, md3.Frames.Length);
        Assert.Equal(new Vector3(-1, -2, -3), md3.Frames[0].Mins);
        Assert.Equal(new Vector3(1, 2, 3), md3.Frames[0].Maxs);
        Assert.Equal(10f, md3.Frames[0].Radius);
        Assert.Equal(11f, md3.Frames[1].Radius);
        Assert.Equal("frame0", md3.Frames[0].Name);

        Assert.Single(md3.Surfaces);
        Assert.Equal("surface0", md3.Surfaces[0].Name);
        Assert.Equal("textures/test_shader", md3.Surfaces[0].Shaders.Single());
    }

    [Fact]
    public void Synthetic_Tags_ArePivotedFromFrameMajorToTagMajor()
    {
        Md3Data md3 = Md3Reader.Read(BuildSynthetic());

        Assert.Equal(2, md3.Tags.Count);
        Assert.Equal("tag_one", md3.Tags[0].Name);
        Assert.Equal("tag_two", md3.Tags[1].Name);

        // Tag t's transform for frame f carries origin (f, t, 0) — proves the (f * numTags + t) layout.
        for (int t = 0; t < 2; t++)
        for (int f = 0; f < 2; f++)
            Assert.Equal(new Vector3(f, t, 0), md3.Tags[t].Transforms[f].Origin);

        Assert.True(md3.TagsByName.ContainsKey("tag_one"));
        Assert.Same(md3.Tags[1], md3.TagsByName["tag_two"]);
        // identity axes round-trip
        Assert.Equal(new Vector3(1, 0, 0), md3.Tags[0].Transforms[0].AxisX);
        Assert.Equal(new Vector3(0, 0, 1), md3.Tags[0].Transforms[0].AxisZ);
    }

    [Fact]
    public void Synthetic_Vertices_ScaledBy1Over64_AndNormalsDecodeLatLong()
    {
        Md3Data md3 = Md3Reader.Read(BuildSynthetic());
        Md3Surface s = md3.Surfaces[0];

        Assert.Equal(3, s.VertexCount);
        Assert.Equal(2, s.FrameVertices.Length);          // one block per model frame
        Assert.Equal(3, s.FrameVertices[0].Length);
        Assert.Equal(new[] { 0, 1, 2 }, s.Triangles);
        Assert.Equal(new Vector2(0.25f, 0.5f), s.TexCoords[1]);

        // short (64,128,-64) * (1/64) = (1,2,-1)
        Assert.Equal(new Vector3(1f, 2f, -1f), s.FrameVertices[0][0].Position);

        // lat/long byte normals — tolerance, NOT bit-exact (MathF vs DP's mod_md3_sin table).
        AssertVecApprox(new Vector3(0, 0, 1), s.FrameVertices[0][0].Normal);  // pitch 0
        AssertVecApprox(new Vector3(1, 0, 0), s.FrameVertices[0][1].Normal);  // pitch 64 (90°), yaw 0
        AssertVecApprox(new Vector3(0, 1, 0), s.FrameVertices[0][2].Normal);  // pitch 64, yaw 64
        foreach (var v in s.FrameVertices.SelectMany(fr => fr))
            Assert.Equal(1f, v.Normal.Length(), 3);
    }

    private static void AssertVecApprox(Vector3 expected, Vector3 actual, float tol = 1e-3f)
        => Assert.True((expected - actual).Length() <= tol, $"expected ~{expected}, got {actual}");

    // ---------------------------------------------------------------- synthetic: error paths

    [Fact]
    public void WrongMagic_Throws()
        => Assert.Throws<AssetParseException>(() => Md3Reader.Read(BuildSynthetic(magic: "IDPX")));

    [Fact]
    public void WrongVersion_Throws()
    {
        // DP hard-checks version == 15 (MD3VERSION, model_alias.h).
        Assert.Throws<AssetParseException>(() => Md3Reader.Read(BuildSynthetic(version: 14)));
        Assert.Throws<AssetParseException>(() => Md3Reader.Read(BuildSynthetic(version: 16)));
    }

    [Fact]
    public void TooSmallBuffer_Throws()
        => Assert.Throws<AssetParseException>(() => Md3Reader.Read(new byte[HeaderSize - 1]));

    [Fact]
    public void MeshWithWrongIdent_Throws()
        => Assert.Throws<AssetParseException>(() => Md3Reader.Read(BuildSynthetic(meshMagic: "XXXX")));

    [Fact]
    public void MeshFrameCountMismatch_Throws()
        => Assert.Throws<AssetParseException>(() => Md3Reader.Read(BuildSynthetic(meshFrames: 1)));

    [Fact]
    public void TriangleIndexOutOfRange_Throws()
        => Assert.Throws<AssetParseException>(() => Md3Reader.Read(BuildSynthetic(triIndex2: 99)));

    // ---------------------------------------------------------------- real assets (skip-if-missing)

    private static readonly Lazy<Md3Data?> RealMd3 = new(() =>
    {
        if (!Directory.Exists(DataDir)) return null;
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountGameDir(DataDir)) return null;
        string? path = vfs.Find("models/", "md3").FirstOrDefault();
        return path is null ? null : Md3Reader.Read(vfs.ReadBytes(path));
    });

    [Fact]
    public void RealAsset_StructuralInvariants()
    {
        Md3Data? md3 = RealMd3.Value;
        if (md3 is null) return; // skip-if-missing

        Assert.True(md3.FrameCount >= 1);
        Assert.Equal(md3.FrameCount, md3.Frames.Length);
        Assert.True(md3.Surfaces.Length >= 1);

        foreach (Md3Surface s in md3.Surfaces)
        {
            Assert.Equal(md3.FrameCount, s.FrameVertices.Length);
            Assert.Equal(s.VertexCount, s.TexCoords.Length);
            Assert.True(s.Triangles.Length > 0 && s.Triangles.Length % 3 == 0);
            Assert.All(s.Triangles, i => Assert.InRange(i, 0, s.VertexCount - 1));
            foreach (var frame in s.FrameVertices)
            {
                Assert.Equal(s.VertexCount, frame.Length);
                foreach (var v in frame)
                    Assert.Equal(1f, v.Normal.Length(), 2); // unit normals, tolerance only
            }
        }

        // Per-frame bounds sanity: radius non-negative, maxs >= mins on every axis.
        foreach (Md3Frame f in md3.Frames)
        {
            Assert.True(f.Radius >= 0f);
            Assert.True(f.Maxs.X >= f.Mins.X && f.Maxs.Y >= f.Mins.Y && f.Maxs.Z >= f.Mins.Z);
        }

        // Tags carry one transform per model frame.
        foreach (Md3Tag t in md3.Tags)
            Assert.Equal(md3.FrameCount, t.Transforms.Length);
    }
}
