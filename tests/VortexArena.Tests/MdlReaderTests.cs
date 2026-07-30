using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using XonoticGodot.Formats;
using XonoticGodot.Formats.Mdl;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// Tests <see cref="MdlReader"/> — the Quake1 MDL ("IDPO" v6) importer. The synthetic cases build a minimal
/// MDL in memory so they run everywhere (CI-portable, no assets); the real-asset cases parse the shipped
/// casing/chunk MDLs and silently no-op when the reference checkout is absent (mirrors <c>AssetParserTests</c>).
/// </summary>
public class MdlReaderTests
{
    private const string Pk3Dir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data\xonotic-data.pk3dir";

    // ── Synthetic minimal MDL (no assets needed) ───────────────────────────────────────────────────

    /// <summary>Parses a hand-built 1-skin / 3-vert / 1-tri / 1-frame MDL and checks every decoded field.</summary>
    [Fact]
    public void Read_MinimalMdl_DecodesGeometrySkinAndUvs()
    {
        byte[] bytes = BuildMinimalMdl(facesFront: 1, vert1OnSeam: 0);
        MdlData mdl = MdlReader.Read(bytes);

        Assert.Equal(3, mdl.VertexCount);
        Assert.Single(mdl.Frames);
        Assert.Equal(3, mdl.Corners.Length);        // 1 triangle → 3 non-indexed corners
        Assert.Equal("frame0", mdl.Name);
        Assert.Equal(2, mdl.SkinWidth);
        Assert.Equal(2, mdl.SkinHeight);

        // Positions decode as byte * scale + origin (scale 0.1, origin 0): (10,0,0)->(1,0,0) etc.
        MdlVertex[] v = mdl.Frames[0].Vertices;
        AssertVec(new Vector3(1f, 0f, 0f), v[0].Position);
        AssertVec(new Vector3(0f, 1f, 0f), v[1].Position);
        AssertVec(new Vector3(0f, 0f, 1f), v[2].Position);
        // Normal index 0 → the first Quake anorm (-0.525731, 0, 0.850651).
        AssertVec(new Vector3(-0.525731f, 0f, 0.850651f), v[0].Normal);
        Assert.All(v, x => Assert.True(MathF.Abs(x.Normal.Length() - 1f) < 1e-3f, "anorms are unit vectors"));

        // Skin: indices [0,15,255,1] decoded through the Quake palette to opaque RGBA.
        Assert.Equal(2 * 2 * 4, mdl.SkinRgba.Length);
        AssertRgba(0, 0, 0, mdl.SkinRgba, 0);         // palette[0]   = black
        AssertRgba(235, 235, 235, mdl.SkinRgba, 1);   // palette[15]  = light grey
        AssertRgba(159, 91, 83, mdl.SkinRgba, 2);     // palette[255] = reddish-brown
        AssertRgba(15, 15, 15, mdl.SkinRgba, 3);      // palette[1]

        // UVs: st/skin (invW=invH=0.5), facesfront=1 so no seam shift.
        Assert.Equal(new Vector2(0f, 0f), mdl.Corners[0].Uv);       // vert0 st (0,0)
        Assert.Equal(new Vector2(0.5f, 0f), mdl.Corners[1].Uv);     // vert1 st (1,0)
        Assert.Equal(new Vector2(0f, 0.5f), mdl.Corners[2].Uv);     // vert2 st (0,1)
        Assert.Equal(0, mdl.Corners[0].Vertex);
        Assert.Equal(1, mdl.Corners[1].Vertex);
        Assert.Equal(2, mdl.Corners[2].Vertex);
    }

    /// <summary>A back-facing (facesfront=0) triangle's on-seam vertex samples the far skin half: U += 0.5.</summary>
    [Fact]
    public void Read_BackfaceOnSeamVertex_ShiftsUByHalf()
    {
        byte[] bytes = BuildMinimalMdl(facesFront: 0, vert1OnSeam: 1);
        MdlData mdl = MdlReader.Read(bytes);

        // vert1 st (1,0) → U = 1*0.5 + 0.5 (seam shift) = 1.0; vert0/vert2 are not on the seam → unshifted.
        Assert.Equal(new Vector2(0f, 0f), mdl.Corners[0].Uv);
        Assert.Equal(new Vector2(1.0f, 0f), mdl.Corners[1].Uv);
        Assert.Equal(new Vector2(0f, 0.5f), mdl.Corners[2].Uv);
    }

    [Fact]
    public void Read_WrongMagic_Throws()
    {
        byte[] bytes = BuildMinimalMdl(1, 0);
        bytes[0] = (byte)'X';
        Assert.Throws<AssetParseException>(() => MdlReader.Read(bytes));
    }

    [Fact]
    public void Read_Truncated_Throws()
    {
        byte[] bytes = BuildMinimalMdl(1, 0);
        Assert.Throws<AssetParseException>(() => MdlReader.Read(bytes.AsSpan(0, bytes.Length - 8).ToArray()));
    }

    // ── Real shipped MDLs (self-skip without a checkout) ───────────────────────────────────────────

    [Theory]
    [InlineData("models/casing_shell.mdl", 50, 48, 64, 128)]
    [InlineData("models/casing_steel.mdl", 50, 48, 64, 128)]
    [InlineData("models/gibs/chunk.mdl", 10, 16, 32, 32)]
    public void Read_ShippedMdl_ParsesToRealGeometryAndSkin(
        string vpath, int verts, int tris, int skinW, int skinH)
    {
        string path = Path.Combine(Pk3Dir, vpath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return; // no checkout → no-op

        MdlData mdl = MdlReader.Read(File.ReadAllBytes(path));

        Assert.Equal(verts, mdl.VertexCount);
        Assert.Equal(tris * 3, mdl.Corners.Length);
        Assert.Equal(skinW, mdl.SkinWidth);
        Assert.Equal(skinH, mdl.SkinHeight);
        Assert.NotEmpty(mdl.Frames);
        Assert.Equal(skinW * skinH * 4, mdl.SkinRgba.Length);

        // Every corner references a valid vertex; every position/normal is finite; normals are unit length.
        MdlVertex[] frame0 = mdl.Frames[0].Vertices;
        Assert.Equal(verts, frame0.Length);
        Assert.All(mdl.Corners, c => Assert.InRange(c.Vertex, 0, verts - 1));
        Assert.All(frame0, v =>
        {
            Assert.True(IsFinite(v.Position), "position is finite");
            Assert.True(MathF.Abs(v.Normal.Length() - 1f) < 1e-2f, "normal is unit length");
        });
        // The skin is fully opaque (MDL skins have no alpha) and not all-black.
        Assert.True(Enumerable.Range(0, skinW * skinH).All(i => mdl.SkinRgba[i * 4 + 3] == 255), "skin is opaque");
        Assert.Contains(Enumerable.Range(0, skinW * skinH),
            i => mdl.SkinRgba[i * 4] != 0 || mdl.SkinRgba[i * 4 + 1] != 0 || mdl.SkinRgba[i * 4 + 2] != 0);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static void AssertVec(Vector3 expected, Vector3 actual)
    {
        Assert.True((expected - actual).Length() < 1e-4f, $"expected {expected}, got {actual}");
    }

    private static void AssertRgba(byte r, byte g, byte b, byte[] rgba, int texel)
    {
        int o = texel * 4;
        Assert.Equal(r, rgba[o]);
        Assert.Equal(g, rgba[o + 1]);
        Assert.Equal(b, rgba[o + 2]);
        Assert.Equal((byte)255, rgba[o + 3]);
    }

    /// <summary>
    /// Build a minimal but valid Quake1 MDL: 1 single skin (2x2, indices [0,15,255,1]), 3 stverts, 1 triangle
    /// (vertindex 0,1,2), 1 single frame named "frame0" with byte verts (10,0,0)/(0,10,0)/(0,0,10) and normal
    /// index 0. <paramref name="facesFront"/> and <paramref name="vert1OnSeam"/> drive the seam-UV path.
    /// </summary>
    private static byte[] BuildMinimalMdl(int facesFront, int vert1OnSeam)
    {
        using var ms = new MemoryStream();
        void I32(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); ms.Write(b); }
        void F32(float v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); ms.Write(b); }

        // ── header (84 bytes) ──
        ms.Write(Encoding.ASCII.GetBytes("IDPO"));      // ident
        I32(6);                                          // version
        F32(0.1f); F32(0.1f); F32(0.1f);                 // scale
        F32(0f); F32(0f); F32(0f);                       // scale_origin
        F32(0f);                                         // boundingradius
        F32(0f); F32(0f); F32(0f);                       // eyeposition
        I32(1);                                          // numskins
        I32(2);                                          // skinwidth
        I32(2);                                          // skinheight
        I32(3);                                          // numverts
        I32(1);                                          // numtris
        I32(1);                                          // numframes
        I32(0);                                          // synctype
        I32(0);                                          // flags
        F32(0f);                                         // size

        // ── skin 0 (single): type + 2x2 indices ──
        I32(0);                                          // ALIAS_SKIN_SINGLE
        ms.Write(new byte[] { 0, 15, 255, 1 });

        // ── stverts: (onseam, s, t) ──
        I32(0); I32(0); I32(0);                          // vert0 (0,0)
        I32(vert1OnSeam); I32(1); I32(0);                // vert1 (1,0)
        I32(0); I32(0); I32(1);                          // vert2 (0,1)

        // ── triangle: facesfront + vertindex[3] ──
        I32(facesFront); I32(0); I32(1); I32(2);

        // ── frame 0 (single): type + daliasframe(bboxmin,bboxmax,name[16]) + 3 trivertx ──
        I32(0);                                          // ALIAS_SINGLE
        ms.Write(new byte[] { 0, 0, 0, 0 });             // bboxmin trivertx
        ms.Write(new byte[] { 255, 255, 255, 0 });       // bboxmax trivertx
        byte[] name = new byte[16];
        Encoding.ASCII.GetBytes("frame0").CopyTo(name, 0);
        ms.Write(name);
        ms.Write(new byte[] { 10, 0, 0, 0 });            // vert0: v(10,0,0) normalindex 0
        ms.Write(new byte[] { 0, 10, 0, 0 });            // vert1
        ms.Write(new byte[] { 0, 0, 10, 0 });            // vert2

        return ms.ToArray();
    }
}
