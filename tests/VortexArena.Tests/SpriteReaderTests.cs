using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using XonoticGodot.Formats;
using XonoticGodot.Formats.Sprites;
using XonoticGodot.Formats.Vfs;
using Xunit;

namespace XonoticGodot.Tests;

/// <summary>
/// T31 — first-ever coverage for <see cref="SpriteReader"/> (port of Darkplaces <c>Mod_IDSP_Load</c> /
/// <c>Mod_IDS2_Load</c> / <c>Mod_Sprite_SharedSetup</c>, model_sprite.c). Pins: the magic/version
/// dispatch (IDSP v1 quake / v2 half-life / v32 spr32; IDS2 v2 .sp2), spr32's BGRA→RGBA swap, the HL
/// 256-color palette requirement + per-rendermode alpha rules (IndexAlpha = colour 765-767 with
/// alpha=index; AlphaTest = pal[255] transparent; unknown rendermode errors), SPR_SINGLE/SPR_GROUP
/// frame flattening with intervals, and sp2's negated origin_x + forced SPR_VP_PARALLEL.
/// </summary>
public class SpriteReaderTests
{
    private const string DataDir = @"C:\Users\Bryan\Projects\Xonotic\XonoticGodot\assets\data";

    // ---------------------------------------------------------------- synthetic builders

    private static void Add(List<byte> b, int v) => b.AddRange(BitConverter.GetBytes(v));
    private static void Add(List<byte> b, float v) => b.AddRange(BitConverter.GetBytes(v));

    /// <summary>dsprite_t header (36 bytes): ident, version, type, boundingradius, width, height, numframes, beamlength, synctype.</summary>
    private static List<byte> IdspHeader(int version, int type, int numFrames)
    {
        var b = new List<byte>();
        b.AddRange(Encoding.ASCII.GetBytes("IDSP"));
        Add(b, version);
        Add(b, type);
        Add(b, 32f);          // boundingradius
        Add(b, 2); Add(b, 2); // width/height (informational)
        Add(b, numFrames);
        Add(b, 0f);           // beamlength
        Add(b, 0);            // synctype
        return b;
    }

    private static void AddFrame(List<byte> b, int originX, int originY, int w, int h, byte[] pixels)
    {
        Add(b, originX); Add(b, originY); Add(b, w); Add(b, h);
        b.AddRange(pixels);
    }

    [Fact]
    public void Spr32_SwapsBgraToRgba_AndDerivesQuad()
    {
        List<byte> b = IdspHeader(version: 32, type: 2, numFrames: 1);
        Add(b, 0); // SPR_SINGLE
        // 2x1 BGRA pixels: (B=1,G=2,R=3,A=4) and (B=10,G=20,R=30,A=40)
        AddFrame(b, originX: -1, originY: 1, w: 2, h: 1, pixels: new byte[] { 1, 2, 3, 4, 10, 20, 30, 40 });

        SpriteData spr = SpriteReader.Read(b.ToArray());

        Assert.Equal(SpriteFormat.Spr32, spr.Format);
        Assert.Equal(SpriteType.VpParallel, spr.SpriteType);
        SpriteFrame f = spr.Frames.Single();
        Assert.NotNull(f.Rgba);
        Assert.Equal(new byte[] { 3, 2, 1, 4, 30, 20, 10, 40 }, f.Rgba); // R<->B swapped
        Assert.Null(f.Indices);
        // quad bounds: left = originX, right = originX+w, up = originY, down = originY-h
        Assert.Equal(-1, f.QuadLeft);
        Assert.Equal(1, f.QuadRight);
        Assert.Equal(1, f.QuadUp);
        Assert.Equal(0, f.QuadDown);
    }

    [Fact]
    public void SprV1_KeepsRawPaletteIndices_AndFlattensGroups()
    {
        // One SPR_GROUP slot of 2 frames: numframes, 2 intervals, then 2 dspriteframe_t + pixels.
        List<byte> b = IdspHeader(version: 1, type: 0, numFrames: 1);
        Add(b, 1);          // SPR_GROUP
        Add(b, 2);          // group frame count
        Add(b, 0.1f); Add(b, 0.25f);
        AddFrame(b, 0, 0, 1, 1, new byte[] { 7 });
        AddFrame(b, 0, 0, 1, 1, new byte[] { 9 });

        SpriteData spr = SpriteReader.Read(b.ToArray());

        Assert.Equal(SpriteFormat.Spr, spr.Format);
        Assert.Equal(2, spr.FrameCount);                       // flattened (DP realframes)
        Assert.Single(spr.GroupRanges);
        SpriteGroup g = spr.GroupRanges[0];
        Assert.Equal(0, g.FirstFrame);
        Assert.Equal(2, g.FrameCount);
        Assert.Equal(new[] { 0.1f, 0.25f }, g.Intervals);
        // Quake-palette sprites keep raw indices (the palette is external, gfx/palette.lmp).
        Assert.Equal(new byte[] { 7 }, spr.Frames[0].Indices);
        Assert.Equal(new byte[] { 9 }, spr.Frames[1].Indices);
        Assert.Null(spr.Frames[0].Rgba);
    }

    // ---- Half-Life (IDSP v2): header + rendermode + embedded 256-color palette ----

    private static List<byte> HlSprite(int rendermode, byte pixel)
    {
        var b = new List<byte>();
        b.AddRange(Encoding.ASCII.GetBytes("IDSP"));
        Add(b, 2);            // version (HL)
        Add(b, 2);            // type
        Add(b, rendermode);   // rendermode sits after type in the HL header
        Add(b, 32f);          // boundingradius
        Add(b, 1); Add(b, 1); // width/height
        Add(b, 1);            // numframes
        Add(b, 0f);           // beamlength
        Add(b, 0);            // synctype
        b.AddRange(BitConverter.GetBytes((short)256)); // palette colour count MUST be 256
        for (int i = 0; i < 256; i++)                  // palette: entry i = (i, 100, 200)
        {
            b.Add((byte)i); b.Add(100); b.Add(200);
        }
        Add(b, 0); // SPR_SINGLE
        AddFrame(b, 0, 0, 1, 1, new[] { pixel });
        return b;
    }

    [Fact]
    public void HlSprite_Opaque_ExpandsThroughPalette_FullAlpha()
    {
        SpriteData spr = SpriteReader.Read(HlSprite(rendermode: 0, pixel: 5).ToArray());

        Assert.Equal(SpriteFormat.SprHl, spr.Format);
        Assert.Equal(SpriteHlRenderMode.Opaque, spr.HlRenderMode);
        Assert.False(spr.Additive);
        Assert.Equal(new byte[] { 5, 100, 200, 255 }, spr.Frames[0].Rgba);
    }

    [Fact]
    public void HlSprite_Additive_SetsAdditiveFlag()
    {
        SpriteData spr = SpriteReader.Read(HlSprite(rendermode: 1, pixel: 0).ToArray());
        Assert.Equal(SpriteHlRenderMode.Additive, spr.HlRenderMode);
        Assert.True(spr.Additive);
    }

    [Fact]
    public void HlSprite_IndexAlpha_ColourFromLastEntry_AlphaIsIndex()
    {
        // SPRHL_INDEXALPHA: colour = palette bytes 765-767 (the LAST entry = (255,100,200)), alpha = index.
        SpriteData spr = SpriteReader.Read(HlSprite(rendermode: 2, pixel: 42).ToArray());
        Assert.Equal(new byte[] { 255, 100, 200, 42 }, spr.Frames[0].Rgba);
    }

    [Fact]
    public void HlSprite_AlphaTest_Index255IsTransparent()
    {
        SpriteData spr = SpriteReader.Read(HlSprite(rendermode: 3, pixel: 255).ToArray());
        Assert.Equal(0, spr.Frames[0].Rgba![3]);   // pal[255] alpha forced 0
        SpriteData opaquePixel = SpriteReader.Read(HlSprite(rendermode: 3, pixel: 7).ToArray());
        Assert.Equal(255, opaquePixel.Frames[0].Rgba![3]);
    }

    [Fact]
    public void HlSprite_UnknownRendermode_Throws()
        => Assert.Throws<AssetParseException>(() => SpriteReader.Read(HlSprite(rendermode: 4, pixel: 0).ToArray()));

    [Fact]
    public void HlSprite_WrongPaletteCount_Throws()
    {
        List<byte> b = HlSprite(rendermode: 0, pixel: 0);
        byte[] raw = b.ToArray();
        // patch the palette colour count (2 bytes after the 40-byte HL header) to 128 — Host_Error in DP.
        BitConverter.GetBytes((short)128).CopyTo(raw, 40);
        Assert.Throws<AssetParseException>(() => SpriteReader.Read(raw));
    }

    // ---- IDS2 (.sp2) ----

    [Fact]
    public void Sp2_NegatesOriginX_ForcesVpParallel_AndCarriesExternalName()
    {
        var b = new List<byte>();
        b.AddRange(Encoding.ASCII.GetBytes("IDS2"));
        Add(b, 2);  // version
        Add(b, 1);  // numframes
        // dsprite2frame_t { width, height, origin_x, origin_y, name[64] }
        Add(b, 16); Add(b, 8); Add(b, 4); Add(b, 6);
        var name = new byte[64];
        Encoding.ASCII.GetBytes("sprites/bluebase.pcx").CopyTo(name, 0);
        b.AddRange(name);

        SpriteData spr = SpriteReader.Read(b.ToArray());

        Assert.Equal(SpriteFormat.Sp2, spr.Format);
        Assert.Equal(SpriteType.VpParallel, spr.SpriteType); // DP forces it regardless of stored type
        SpriteFrame f = spr.Frames.Single();
        Assert.Equal(-4, f.OriginX);                          // sp2 origin_x sign is opposite spr (left = -origin[0])
        Assert.Equal(6, f.OriginY);
        Assert.Equal(16, f.Width);
        Assert.Equal(8, f.Height);
        Assert.Equal("sprites/bluebase.pcx", f.ExternalImage);
        Assert.Null(f.Rgba);
        Assert.Null(f.Indices);
    }

    [Fact]
    public void Sp2_WrongVersion_Throws()
    {
        var b = new List<byte>();
        b.AddRange(Encoding.ASCII.GetBytes("IDS2"));
        Add(b, 3); Add(b, 1);
        Assert.Throws<AssetParseException>(() => SpriteReader.Read(b.ToArray()));
    }

    // ---- generic error paths ----

    [Fact]
    public void WrongMagic_Throws()
        => Assert.Throws<AssetParseException>(() => SpriteReader.Read(Encoding.ASCII.GetBytes("NOPE0000")));

    [Fact]
    public void IdspUnsupportedVersion_Throws()
    {
        List<byte> b = IdspHeader(version: 5, type: 0, numFrames: 1);
        Assert.Throws<AssetParseException>(() => SpriteReader.Read(b.ToArray()));
    }

    [Fact]
    public void TruncatedPixels_Throws()
    {
        List<byte> b = IdspHeader(version: 1, type: 0, numFrames: 1);
        Add(b, 0); // SPR_SINGLE
        Add(b, 0); Add(b, 0); Add(b, 100); Add(b, 100); // 100x100 frame but no pixel data follows
        Assert.Throws<AssetParseException>(() => SpriteReader.Read(b.ToArray()));
    }

    [Fact]
    public void TooSmall_Throws()
        => Assert.Throws<AssetParseException>(() => SpriteReader.Read(new byte[4]));

    // ---------------------------------------------------------------- real assets (skip-if-missing)

    [Fact]
    public void RealAssets_ParseAcrossAllThreeShippedFormats()
    {
        if (!Directory.Exists(DataDir)) return;
        using var vfs = new VirtualFileSystem();
        if (!vfs.MountGameDir(DataDir)) return;

        // IDSP v1 (models/misc/chatbubble.spr and friends)
        string? sprPath = vfs.Find("models/", "spr").FirstOrDefault();
        if (sprPath is not null)
        {
            SpriteData spr = SpriteReader.Read(vfs.ReadBytes(sprPath));
            Assert.True(spr.FrameCount >= 1);
            Assert.Equal(spr.Frames.Length, spr.FrameCount);
            Assert.True(spr.GroupRanges.Length >= 1);
        }

        // spr32 (sprites/*.spr32)
        string? spr32Path = vfs.Find("sprites/", "spr32").FirstOrDefault();
        if (spr32Path is not null)
        {
            SpriteData s32 = SpriteReader.Read(vfs.ReadBytes(spr32Path));
            Assert.Equal(SpriteFormat.Spr32, s32.Format);
            foreach (SpriteFrame f in s32.Frames)
            {
                Assert.NotNull(f.Rgba);
                Assert.Equal(f.Width * f.Height * 4, f.Rgba!.Length);
            }
        }

        // sp2 (sprites/*.sp2)
        string? sp2Path = vfs.Find("sprites/", "sp2").FirstOrDefault();
        if (sp2Path is not null)
        {
            SpriteData sp2 = SpriteReader.Read(vfs.ReadBytes(sp2Path));
            Assert.Equal(SpriteFormat.Sp2, sp2.Format);
            Assert.Equal(SpriteType.VpParallel, sp2.SpriteType);
            Assert.All(sp2.Frames, f => Assert.False(string.IsNullOrEmpty(f.ExternalImage)));
        }
    }
}
